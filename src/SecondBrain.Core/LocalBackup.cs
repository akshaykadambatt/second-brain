using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecondBrain.Core;

public sealed record BackupFile(string Path, long Bytes, string Sha256);
public sealed record BackupManifest(int Schema, DateTimeOffset CreatedUtc, BackupFile[] Files);
public sealed record StorageSize(string Category, long Bytes, int Files);

// Call while application writers are stopped. External changes cause verification to fail.
// Only a completed, hash-verified snapshot is published; restore never overwrites a folder.
public static class LocalBackup
{
    private const string ManifestName = "secondbrain-backup.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Root(string path)
    {
        var full = Path.GetFullPath(path);
        for (var part = full; part is not null; part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked folders and files are not supported for storage operations.");
        return Path.TrimEndingDirectorySeparator(full);
    }
    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Safe(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') || relative.StartsWith('/') || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("Invalid backup path.");
        return Root(VaultFiles.SafePath(root, relative));
    }
    private static IEnumerable<string> Files(string root)
    {
        Root(root);
        if (!Directory.Exists(root)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            Root(entry);
            if (Directory.Exists(entry)) { foreach (var file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static bool Skip(string relative, bool data) => data
        ? relative.Equals("instance.lock", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)
            || (relative.StartsWith("recordings/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith("/recording.lock", StringComparison.OrdinalIgnoreCase))
        : relative.Equals(".secondbrain/maintenance.lock", StringComparison.OrdinalIgnoreCase)
            || (relative.StartsWith(".secondbrain/history.git/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    private static BackupFile Describe(string root, string file)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Path.GetRelativePath(root, file).Replace('\\', '/'), input.Length, Convert.ToHexString(SHA256.HashData(input)));
    }
    private static void Copy(string source, string target)
    {
        Root(source); Root(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
    public static string Create(string data, string vault, string executable, string parent)
    {
        data = Root(data); vault = Root(vault); executable = Root(executable); parent = Root(parent);
        if (!Directory.Exists(data) || !Directory.Exists(vault) || !File.Exists(executable)) throw new IOException("Data, vault and executable must be available.");
        if (Within(parent, data) || Within(parent, vault)) throw new IOException("Choose a backup destination outside data and the vault.");
        if (Within(data, vault)) throw new IOException("The vault cannot contain the app data directory. Choose a separate vault folder.");
        var destination = Path.Combine(parent, "SecondBrain-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var partial = destination + ".partial"; Directory.CreateDirectory(partial);
        List<(string Source, string Target)> Inventory()
        {
            var list = new List<(string, string)> { (executable, "SecondBrain.exe") };
            foreach (var file in Files(data).Where(p => !Within(p, vault)))
            { var relative = Path.GetRelativePath(data, file).Replace('\\', '/'); if (!Skip(relative, true)) list.Add((file, "data/" + relative)); }
            foreach (var file in Files(vault))
            { var relative = Path.GetRelativePath(vault, file).Replace('\\', '/'); if (!Skip(relative, false)) list.Add((file, "Vault/" + relative)); }
            if (list.Count > 100000) throw new IOException("Backup exceeds 100,000 files.");
            return list;
        }
        var sources = Inventory(); var copied = new List<BackupFile>();
        foreach (var (source, target) in sources)
        { var path = Safe(partial, target); Copy(source, path); copied.Add(Describe(partial, path)); }
        // Re-enumerate and hash original bytes after the copy, including Git refs and objects.
        if (!sources.SequenceEqual(Inventory())) throw new IOException("Files changed during backup. The incomplete .partial folder is not a valid backup; retry after editing stops.");
        for (var i = 0; i < sources.Count; i++)
        {
            var source = Describe(Path.GetDirectoryName(sources[i].Source)!, sources[i].Source);
            if (source.Bytes != copied[i].Bytes || source.Sha256 != copied[i].Sha256) throw new IOException("A file changed during backup; retry after editing stops.");
        }
        VaultTransaction.SaveJson(Path.Combine(partial, ManifestName), new BackupManifest(1, DateTimeOffset.UtcNow, copied.ToArray()));
        Verify(partial); Directory.Move(partial, destination); return destination;
    }
    public static BackupManifest Verify(string backup)
    {
        backup = Root(backup); var manifestPath = Safe(backup, ManifestName);
        if (new FileInfo(manifestPath).Length > 32_000_000) throw new InvalidDataException("Backup manifest is too large.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Missing backup manifest.");
        if (manifest.Schema != 1 || manifest.Files is null || manifest.Files.Length > 100000 || manifest.Files.Length == 0) throw new InvalidDataException("Unsupported backup manifest.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (item is null || string.IsNullOrEmpty(item.Path) || !seen.Add(item.Path) || !(item.Path == "SecondBrain.exe" || item.Path.StartsWith("data/", StringComparison.Ordinal) || item.Path.StartsWith("Vault/", StringComparison.Ordinal))) throw new InvalidDataException("Invalid or duplicate backup entry.");
            var actual = Describe(backup, Safe(backup, item.Path));
            if (actual != item) throw new InvalidDataException("Backup verification failed: " + item.Path);
        }
        if (!seen.Contains("SecondBrain.exe") || !seen.Contains("data/settings.json") || !manifest.Files.Any(f => f.Path.StartsWith("Vault/"))) throw new InvalidDataException("Backup is missing required app, settings or vault files.");
        var expected = seen.Append(ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Files(backup).Select(p => Path.GetRelativePath(backup, p).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected)) throw new InvalidDataException("Backup contains unexpected files.");
        return manifest;
    }
    public static string Restore(string backup, string parent)
    {
        backup = Root(backup); parent = Root(parent);
        if (backup.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a completed backup, not an incomplete .partial folder.");
        if (Within(parent, backup)) throw new IOException("Restore outside the backup folder.");
        var manifest = Verify(backup);
        var destination = Path.Combine(parent, "SecondBrain-restored-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var partial = destination + ".partial"; Directory.CreateDirectory(partial);
        foreach (var item in manifest.Files)
        {
            var path = Safe(partial, item.Path); Copy(Safe(backup, item.Path), path);
            if (Describe(partial, path) != item) throw new IOException("Backup changed while restoring. No restored copy was published.");
        }
        // The restored app always opens the restored vault, even if the original was external.
        var optionsPath = Safe(partial, "data/vault-settings.json");
        var options = File.Exists(optionsPath) ? JsonNode.Parse(File.ReadAllText(optionsPath)) as JsonObject ?? throw new InvalidDataException("Invalid vault settings.") : new JsonObject();
        options["Folder"] = "Vault";
        VaultTransaction.SaveJson(optionsPath, options);
        Directory.Move(partial, destination); return destination;
    }
    public static StorageSize[] Measure(string data, string vault)
    {
        data = Root(data); vault = Root(vault); var totals = new Dictionary<string, (long Bytes, int Files)>();
        foreach (var root in new[] { data, vault }.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var file in Files(root))
            {
                if (root == data && Within(file, vault)) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var category = root == vault ? relative.StartsWith(".secondbrain/") ? "Vault history and update jobs" : "Vault notes and attachments"
                    : relative.StartsWith("recordings/") ? "Recordings and transcripts" : relative.StartsWith("logs/") ? "Diagnostics" : "Settings, keys and indexes";
                var old = totals.GetValueOrDefault(category); totals[category] = (checked(old.Bytes + new FileInfo(file).Length), old.Files + 1);
            }
        return totals.Select(t => new StorageSize(t.Key, t.Value.Bytes, t.Value.Files)).ToArray();
    }
    public static string[] Recordings(string data)
    {
        var root = Root(Path.Combine(data, "recordings"));
        return !Directory.Exists(root) ? [] : Directory.GetDirectories(root).Select(Root).Where(p => File.Exists(Path.Combine(p, "session.json"))).OrderDescending().ToArray();
    }
    public static void DeleteRecording(string data, string selected)
    {
        var root = Root(Path.Combine(data, "recordings")); selected = Root(selected);
        if (!string.Equals(Path.GetDirectoryName(selected), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a recording directly inside this app's recordings folder.");
        var manifest = RecordingSession.ReadManifest(selected);
        if (manifest.State is not ("Completed" or "Recovered" or "Failed")) throw new IOException("Stop or recover the recording before deleting it.");
        // Validate the entire tree before removal. Hold the same lock used by capture/recovery.
        var files = Files(selected).ToArray();
        var lockPath = Path.Combine(selected, "recording.lock");
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            foreach (var file in files.Where(p => !p.Equals(lockPath, StringComparison.OrdinalIgnoreCase))) File.Delete(Root(file));
        File.Delete(lockPath);
        void Empty(string folder) { foreach (var child in Directory.GetDirectories(Root(folder))) Empty(child); Directory.Delete(Root(folder), false); }
        Empty(selected);
    }
    public static void RecoverRecording(string data, string selected)
    {
        var root = Root(Path.Combine(data, "recordings")); selected = Root(selected);
        if (!string.Equals(Path.GetDirectoryName(selected), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a recording inside this app's recordings folder.");
        _ = Files(selected).ToArray(); // Reject junctions/linked chunks before recovery writes anything.
        var manifest = RecordingSession.Recover(selected); TranscriptLog.Recover(selected, manifest);
    }
}
