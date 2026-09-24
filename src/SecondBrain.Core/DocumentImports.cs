using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record ImportPassage(string Location, string Text);
public sealed record ImportExtraction(ImportPassage[] Passages, string[] Warnings);
public sealed record ImportManifest(int Schema, string Id, string Sha256, string Name, string SourcePath, string Project,
    DateTimeOffset ImportedUtc, string Original, string Note, string[] Warnings);
public sealed record ImportResult(string Name, string State, string Message, ImportManifest? Document = null)
{
    public override string ToString() => $"{Name} · {State}\n{Message}";
}

// Source snapshots and extracted notes publish together. Existing notes are never overwritten.
public static class DocumentImports
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static ImportResult Import(string root, string source, string project, CancellationToken cancellation = default)
    {
        var name = Path.GetFileName(source);
        try
        {
            root = LocalBackup.Root(root); project = project.Trim();
            if (project.Length > 120 || project.Any(char.IsControl) || project.Contains('"') || project.Contains('\''))
                throw new InvalidDataException("Use a project name of at most 120 characters without quotes or control characters.");
            source = LocalBackup.Root(source);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension is not (".md" or ".txt")) throw new InvalidDataException("Choose a Markdown (.md) or text (.txt) file.");
            byte[] bytes;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 2_000_000) throw new InvalidDataException("Text imports are limited to 2 MB per file.");
                bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            }
            cancellation.ThrowIfCancellationRequested();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)); var id = Identity(project, hash);
            var folder = "Imports/" + id; var destination = VaultFiles.SafePath(root, folder);
            var engine = new VaultMaintenance(root);
            using var held = engine.Acquire();
            engine.Initialize();
            if (Directory.Exists(destination))
            {
                var existing = Read(root, folder + "/import.json");
                if (existing.Sha256 != hash || !existing.Project.Equals(project, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(VaultFiles.SafePath(root, existing.Note))
                    || new FileInfo(VaultFiles.SafePath(root, existing.Original)).Length != bytes.Length
                    || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(VaultFiles.SafePath(root, existing.Original)))) != hash)
                    throw new InvalidDataException("Existing import is incomplete or changed. Restore its preserved source before retrying.");
                return new(name, "Already imported", "Identical content in this project; existing source and edited note kept.", existing);
            }
            var extraction = ExtractText(bytes);
            var original = folder + "/Attachments/original" + extension;
            var manifest = new ImportManifest(1, id, hash, name, source, project, DateTimeOffset.UtcNow, original, folder + "/Content.md", extraction.Warnings);
            var note = Render(manifest, extraction);
            // Hidden staging is not indexed. A crash before the final rename cannot expose partial content.
            var staging = VaultFiles.SafePath(root, ".secondbrain/import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                Directory.CreateDirectory(Path.Combine(staging, "Attachments"));
                Write(Path.Combine(staging, "Attachments", "original" + extension), bytes);
                Write(Path.Combine(staging, "Content.md"), Utf8.GetBytes(note));
                Write(Path.Combine(staging, "import.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(staging, destination);
            }
            finally
            {
                // Delete only our known files in our verified, unique staging directory; never recurse.
                if (Directory.Exists(staging))
                {
                    foreach (var file in new[] { "Attachments/original" + extension, "Content.md", "import.json" })
                    { var path = VaultFiles.SafePath(staging, file); if (File.Exists(path)) File.Delete(path); }
                    if (Directory.Exists(Path.Combine(staging, "Attachments"))) Directory.Delete(Path.Combine(staging, "Attachments"));
                    Directory.Delete(staging);
                }
            }
            var message = "Source preserved; searchable note includes original line references. Changed source files create separate imports.";
            try { engine.Git.Checkpoint(engine.Capture(), "Import source document"); }
            catch (Exception) { message += " Imported files are safe, but history checkpoint failed; reconnect the vault to retry."; }
            return new(name, "Imported", message, manifest);
        }
        catch (OperationCanceledException) { return new(name, "Cancelled", "No new import was published."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { return new(name, "Failed", ex.Message); }
    }
    private static string Identity(string project, string hash) => VaultIndex.Hash(project.ToUpperInvariant() + "\n" + hash)[..24];
    private static void Write(string path, byte[] bytes)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
    public static ImportExtraction ExtractText(byte[] bytes)
    {
        var text = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2)
            : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2)
            : Utf8.GetString(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsSpan(3) : bytes);
        if (text.Length > 500_000 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Text is too large or contains binary/control characters. Use UTF-8 or BOM-marked UTF-16 text (up to 500,000 characters).");
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The source has no searchable text.");
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var passages = new List<ImportPassage>(); var buffer = new StringBuilder(); var start = 1; var end = 1;
        void Flush() { if (!string.IsNullOrWhiteSpace(buffer.ToString())) passages.Add(new($"lines {start}–{end}", buffer.ToString().TrimEnd())); buffer.Clear(); }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            for (var offset = 0; offset < Math.Max(1, line.Length); offset += 400)
            {
                var part = line.Substring(offset, Math.Min(400, line.Length - offset));
                if (buffer.Length + part.Length > 450) Flush();
                if (buffer.Length == 0) start = i + 1;
                end = i + 1; buffer.AppendLine(part);
            }
        }
        Flush(); return new(passages.ToArray(), []);
    }
    private static string Plain(string value) => Regex.Replace(value, @"([\\`*_{}\[\]()#+!|<>])", @"\$1");
    private static string Render(ImportManifest manifest, ImportExtraction extraction)
    {
        var title = Plain(manifest.Name[..Math.Min(100, manifest.Name.Length)]);
        var note = new StringBuilder($"---\nproject: \"{manifest.Project}\"\n---\n# Imported: {title}\n\nPreserved source: [Open original](Attachments/original{Path.GetExtension(manifest.Original)})\n\nImport date: {manifest.ImportedUtc:yyyy-MM-dd} (not the date of the source facts).\n\n");
        foreach (var passage in extraction.Passages)
        {
            note.AppendLine($"## {title} · source {passage.Location}\n");
            foreach (var line in passage.Text.Split('\n')) note.AppendLine("> " + Plain(line.TrimEnd('\r')));
            note.AppendLine();
            if (note.Length > 1_900_000) throw new InvalidDataException("Extracted note exceeds the searchable 1.9 MB limit. Split the source and retry.");
        }
        var result = note.ToString();
        if (Utf8.GetByteCount(result) > 1_900_000) throw new InvalidDataException("Extracted note exceeds the searchable 1.9 MB limit. Split the source and retry.");
        return result;
    }
    public static ImportManifest Read(string root, string relative)
    {
        var path = VaultFiles.SafePath(root, relative);
        if (new FileInfo(path).Length > 32_000) throw new InvalidDataException("Import metadata is too large.");
        var value = JsonSerializer.Deserialize<ImportManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("Import metadata is missing.");
        if (value.Schema != 1 || value.Project is null || value.Sha256 is null || !Regex.IsMatch(value.Sha256, "^[A-F0-9]{64}$")
            || value.Id != Identity(value.Project, value.Sha256) || relative != $"Imports/{value.Id}/import.json"
            || value.Note != $"Imports/{value.Id}/Content.md" || value.Original is null
            || (value.Original != $"Imports/{value.Id}/Attachments/original.md" && value.Original != $"Imports/{value.Id}/Attachments/original.txt")
            || string.IsNullOrWhiteSpace(value.Name) || value.Warnings is null)
            throw new InvalidDataException("Import metadata is invalid or unsupported.");
        return value;
    }
    public static ImportResult[] List(string root)
    {
        var folder = LocalBackup.Root(VaultFiles.SafePath(root, "Imports")); if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Take(1000).Select(path =>
        {
            var relative = "Imports/" + Path.GetFileName(path) + "/import.json";
            try { var item = Read(root, relative); return new ImportResult(item.Name, "Saved", "Project: " + (item.Project.Length == 0 ? "unscoped" : item.Project), item); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { return new ImportResult(Path.GetFileName(path), "Unreadable import", ex.Message); }
        }).OrderByDescending(r => r.Document?.ImportedUtc).ToArray();
    }
}
