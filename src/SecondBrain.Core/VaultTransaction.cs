using System.Text.Json;

namespace SecondBrain.Core;

public sealed record VaultChange(string Path, byte[]? Before, byte[] After);
public sealed record VaultJournal(string Operation, string Parent, string Commit, VaultChange[] Changes);
public sealed class VaultConflictException(string message) : IOException(message);
public sealed class SimulatedVaultCrashException : IOException;

public sealed class VaultTransaction(VaultGit git)
{
    private string JournalPath => VaultFiles.SafePath(git.Root, ".secondbrain/transaction.json");
    public bool Pending => File.Exists(JournalPath);
    public static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(temp, path, true);
    }
    public static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null || b.Length == 0 : b is not null && a.AsSpan().SequenceEqual(b);
    // Caller holds the vault's cross-process maintenance lock. Every target is
    // additionally locked against other editors before checking and writing it.
    public void Apply(IReadOnlyDictionary<string, byte[]> baseline, VaultChange[] changes, string message, string operation, Action<int>? afterWrite = null)
    {
        if (Pending) throw new IOException("An interrupted update needs recovery first.");
        var parent = git.Checkpoint(baseline, "Manual edits and source records before update");
        var after = new Dictionary<string, byte[]>(baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes) after[change.Path] = change.After;
        var commit = git.Prepare(after, message + "\n\nOperation: " + operation, parent);
        var journal = new VaultJournal(operation, parent, commit, changes);
        var streams = Lock(changes, requireBefore: true);
        try
        {
            SaveJson(JournalPath, journal);
            try
            {
                for (var i = 0; i < changes.Length; i++) { Write(streams[i], changes[i].After); afterWrite?.Invoke(i); }
                git.Publish(commit, parent); File.Delete(JournalPath);
            }
            catch (Exception ex) when (ex is not SimulatedVaultCrashException)
            {
                // If publication succeeded, recovery only clears the journal.
                if (git.Head == commit) throw;
                for (var i = 0; i < changes.Length; i++) Write(streams[i], changes[i].Before ?? []);
                File.Delete(JournalPath); throw;
            }
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }
    public void Recover()
    {
        if (!Pending) return;
        var journal = JsonSerializer.Deserialize<VaultJournal>(File.ReadAllText(JournalPath)) ?? throw new InvalidDataException("Unreadable recovery journal; backups are retained.");
        if (git.Head == journal.Commit) { File.Delete(JournalPath); return; }
        if (git.Head != journal.Parent) throw new VaultConflictException("History changed during an interrupted update. Recovery journal retained; inspect it before retrying.");
        var streams = Lock(journal.Changes, requireBefore: false);
        try
        {
            for (var i = 0; i < streams.Length; i++)
            {
                var bytes = Bytes(streams[i]); var change = journal.Changes[i];
                if (!Same(change.Before, bytes) && !Same(change.After, bytes))
                    throw new VaultConflictException("Recovery conflict in " + change.Path + ". Your current file was preserved; prior bytes remain in .secondbrain/transaction.json.");
            }
            for (var i = 0; i < streams.Length; i++) Write(streams[i], journal.Changes[i].After);
            git.Publish(journal.Commit, journal.Parent); File.Delete(JournalPath);
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }
    private FileStream[] Lock(VaultChange[] changes, bool requireBefore)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var change in changes)
            {
                var target = VaultFiles.SafePath(git.Root, change.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (change.Before is not null && !File.Exists(target)) throw new VaultConflictException("Note removed while generating/recovering: " + change.Path);
                var stream = new FileStream(target, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); streams.Add(stream);
                if (requireBefore && !Same(change.Before, Bytes(stream))) throw new VaultConflictException("Note edited while generating: " + change.Path);
            }
            return streams.ToArray();
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }
    private static byte[] Bytes(FileStream stream) { stream.Position = 0; using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray(); }
    private static void Write(FileStream stream, byte[] bytes) { stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(true); }
}
