using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record MeetingEvidence(string Id, string Text, string Source, double Seconds);
public sealed record NoteContext(string Path, string Text);
public sealed record MaintenanceInput(Guid MeetingId, string Date, string Transcript, MeetingEvidence[] Evidence, NoteContext[] Notes);
public sealed record EvidenceCitation(string Id, string Quote);
public sealed record MaintenanceItem(string Text, EvidenceCitation[] Sources);
public sealed record NoteUpdate(string Path, MaintenanceItem[] Items);
public sealed record MaintenanceProposal(NoteUpdate[] Updates);
public interface IMaintenanceProvider { Task<MaintenanceProposal> Propose(MaintenanceInput input, CancellationToken cancellation); }
public sealed record AppliedSection(string Path, string Added);
public sealed record MaintenanceReceipt(Guid MeetingId, string Date, string Transcript, string Operation, string State, AppliedSection[] Sections);

public sealed class VaultMaintenance
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public VaultGit Git { get; }
    public VaultTransaction Transaction { get; }
    public string Root => Git.Root;
    public VaultMaintenance(string root) { Git = new(root); Transaction = new(Git); }
    public FileStream Acquire()
    {
        var path = VaultFiles.SafePath(Root, ".secondbrain/maintenance.lock"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Initialize()
    {
        Git.Initialize(); Transaction.Recover();
        VaultFiles.Create(Root, "Knowledge/Meeting updates.md", "# Meeting updates\n\n[[Home]]\n\nDated, AI-derived observations with transcript sources. Verify important facts against the original audio.\n");
        Git.Checkpoint(Capture(), "Manual notes and original meeting records");
    }
    public Dictionary<string, byte[]> Capture()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); var folders = new Stack<string>(); folders.Push(Root); long bytes = 0;
        while (folders.TryPop(out var folder))
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if (!Path.GetFileName(sub).StartsWith('.') && (File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) folders.Push(sub);
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!(path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) == "transcript.jsonl") || Path.GetFileName(path).StartsWith('.')) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("History cannot checkpoint linked note files.");
                var relative = Path.GetRelativePath(Root, path).Replace('\\', '/'); Add(relative);
            }
        }
        var receipts = VaultFiles.SafePath(Root, ".secondbrain/receipts");
        if (Directory.Exists(receipts)) foreach (var file in Directory.EnumerateFiles(receipts, "*.json"))
            if (new FileInfo(file).Length > 0) Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
        return files;
        void Add(string relative)
        {
            var path = VaultFiles.SafePath(Root, relative); var info = new FileInfo(path);
            if (info.Length > 40_000_000 || bytes + info.Length > 100_000_000 || files.Count >= 4000) throw new InvalidDataException("History snapshot exceeds 4,000 files / 100 MB (40 MB per file). No AI update applied.");
            var content = File.ReadAllBytes(path); bytes += content.Length; files[relative] = content;
        }
    }
    public MaintenanceReceipt? Receipt(Guid meeting)
    {
        var path = VaultFiles.SafePath(Root, ReceiptPath(meeting));
        return File.Exists(path) && new FileInfo(path).Length > 0 ? JsonSerializer.Deserialize<MaintenanceReceipt>(File.ReadAllText(path)) : null;
    }
    public MaintenanceReceipt[] Receipts()
    {
        var path = VaultFiles.SafePath(Root, ".secondbrain/receipts");
        return !Directory.Exists(path) ? [] : Directory.EnumerateFiles(path, "*.json").Where(f => new FileInfo(f).Length > 0)
            .Select(f => JsonSerializer.Deserialize<MaintenanceReceipt>(File.ReadAllText(VaultFiles.SafePath(Root, Path.GetRelativePath(Root, f)))))
            .OfType<MaintenanceReceipt>().OrderByDescending(r => r.Date).ToArray();
    }
    private static string ReceiptPath(Guid id) => ".secondbrain/receipts/" + id.ToString("N") + ".json";
    public async Task<MaintenanceReceipt> Process(string summary, IMaintenanceProvider provider, CancellationToken cancellation, Action<int>? afterWrite = null)
    {
        // Single service worker holds Acquire for the operation, including the
        // network wait; ordinary Obsidian file editing remains available.
        Initialize();
        var sourceFolder = Path.GetDirectoryName(summary)?.Replace('\\', '/') ?? throw new InvalidDataException("Choose a meeting summary.");
        if (!sourceFolder.StartsWith("Meetings/", StringComparison.Ordinal) || !summary.EndsWith("/Summary.md", StringComparison.Ordinal)) throw new InvalidDataException("Not an exported meeting summary.");
        var directory = Path.GetDirectoryName(VaultFiles.SafePath(Root, sourceFolder + "/transcript.jsonl"))!;
        var records = TranscriptLog.Read(directory);
        var id = records.FirstOrDefault()?.SessionId ?? throw new InvalidDataException("Meeting has no transcript records.");
        if (id == Guid.Empty || records.Any(e => e.SessionId != id)) throw new InvalidDataException("Mixed meeting identities in transcript.");
        if (Receipt(id) is { } existing) return existing;
        var evidence = records.Select((e, i) => (e, i)).Where(x => x.e.Kind == "Final" && !string.IsNullOrWhiteSpace(x.e.Text))
            .Select(x => new MeetingEvidence($"t{x.i:D6}", x.e.Text, x.e.Source?.ToString() ?? "Unknown", x.e.Start)).ToArray();
        if (evidence.Sum(e => e.Text.Length) > 200_000) throw new InvalidDataException("Meeting exceeds the 200,000-character automatic update limit. Original transcript retained.");
        var sourceBytes = File.ReadAllBytes(Path.Combine(directory, "transcript.jsonl"));
        var date = sourceFolder.Split('/')[1][..10]; if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out _)) throw new InvalidDataException("Meeting date is missing.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var before = Capture(); Git.Checkpoint(before, "Manual edits before meeting analysis");
            var keywords = VaultIndex.Terms(string.Join(' ', evidence.Select(e => e.Text))).ToHashSet();
            var candidates = before.Where(p => p.Key.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !p.Key.StartsWith("Meetings/") && !p.Key.StartsWith("Templates/") && !p.Key.StartsWith("Imports/") && !p.Key.StartsWith("Clients/") && p.Key != "Home.md")
                .Select(p => new NoteContext(p.Key, Utf8.GetString(p.Value))).OrderByDescending(p => p.Path == "Knowledge/Meeting updates.md" ? int.MaxValue : VaultIndex.Terms(p.Text).Distinct().Count(keywords.Contains)).Take(12).ToArray();
            var input = new MaintenanceInput(id, date, sourceFolder + "/Transcript", evidence,
                candidates.Select(n => n with { Text = n.Text.Length > 5000 ? n.Text[..5000] + "\n[Remaining existing note omitted; append only.]" : n.Text }).ToArray());
            var proposal = evidence.Length == 0 ? new MaintenanceProposal([]) : await provider.Propose(input, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var current = Capture();
            if (candidates.Any(n => !current.TryGetValue(n.Path, out var bytes) || !bytes.AsSpan().SequenceEqual(before[n.Path])) || !File.ReadAllBytes(Path.Combine(directory, "transcript.jsonl")).AsSpan().SequenceEqual(sourceBytes))
            {
                if (!File.ReadAllBytes(Path.Combine(directory, "transcript.jsonl")).AsSpan().SequenceEqual(sourceBytes)) throw new VaultConflictException("Source transcript changed during analysis. Retry with the current source.");
                continue;
            }
            var sections = Compile(input, proposal, current);
            var operation = Guid.NewGuid().ToString("N"); var receipt = new MaintenanceReceipt(id, date, input.Transcript, operation, "Applied", sections);
            var changes = sections.Select(s => new VaultChange(s.Path, current[s.Path], Utf8.GetBytes(Utf8.GetString(current[s.Path]) + s.Added))).ToList();
            var receiptPath = ReceiptPath(id); changes.Add(new(receiptPath, current.GetValueOrDefault(receiptPath), JsonSerializer.SerializeToUtf8Bytes(receipt)));
            try { Transaction.Apply(current, changes.ToArray(), "AI meeting update " + date + " · " + sections.Length + " notes", operation, afterWrite); return receipt; }
            catch (VaultConflictException) when (!Transaction.Pending) { /* Recompute from a fresh manual snapshot. */ }
        }
        throw new VaultConflictException("Notes kept changing during analysis. No AI text was applied; retry after editing settles.");
    }
    private static AppliedSection[] Compile(MaintenanceInput input, MaintenanceProposal proposal, Dictionary<string, byte[]> current)
    {
        if (proposal.Updates is null || proposal.Updates.Length > 12) throw new InvalidDataException("Invalid update count.");
        var allowed = input.Notes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal); var ids = input.Evidence.ToDictionary(e => e.Id);
        var updates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); var count = 0;
        foreach (var update in proposal.Updates)
        {
            if (!allowed.Contains(update.Path) || update.Items is null) throw new InvalidDataException("AI suggested a note outside the supplied targets.");
            if (!updates.TryGetValue(update.Path, out var lines)) updates[update.Path] = lines = [];
            foreach (var item in update.Items)
            {
                if (++count > 40 || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 1200 || item.Sources is not { Length: >= 1 and <= 6 }) throw new InvalidDataException("Invalid sourced update item.");
                foreach (var citation in item.Sources)
                    if (!ids.TryGetValue(citation.Id, out var source) || string.IsNullOrWhiteSpace(citation.Quote) || citation.Quote.Length > 600 || !Normalize(source.Text).Contains(Normalize(citation.Quote), StringComparison.Ordinal))
                        throw new InvalidDataException("AI citation does not match its transcript passage; nothing applied.");
                if (Normalize(Utf8.GetString(current[update.Path])).Contains(Normalize(item.Text), StringComparison.Ordinal)) continue;
                var line = "- " + Plain(item.Text) + " " + string.Join(' ', item.Sources.Select(s => $"[[{input.Transcript}#^{s.Id}|source {s.Id}]]"));
                if (!lines.Contains(line)) lines.Add(line);
            }
        }
        return updates.Where(u => u.Value.Count > 0).Select(u => new AppliedSection(u.Key,
            $"\n\n<!-- secondbrain:{input.MeetingId:N}:start -->\n## Meeting update · {input.Date}\n\nAI-derived notes; verify against the linked transcript. [[{input.Transcript}|Meeting source]]\n\n" + string.Join("\n\n", u.Value) + $"\n<!-- secondbrain:{input.MeetingId:N}:end -->\n")).ToArray();
    }
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    private static string Plain(string text) => Normalize(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    public void Revert(Guid meeting, Action<int>? afterWrite = null)
    {
        Initialize(); var receipt = Receipt(meeting) ?? throw new InvalidDataException("Update not found.");
        if (receipt.State == "Reverted") return;
        var current = Capture(); var changes = new List<VaultChange>();
        foreach (var section in receipt.Sections)
        {
            if (!current.TryGetValue(section.Path, out var bytes)) throw new VaultConflictException("Revert conflict: note removed, " + section.Path);
            var text = Utf8.GetString(bytes); var at = text.IndexOf(section.Added, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(section.Added, at + 1, StringComparison.Ordinal) >= 0) throw new VaultConflictException("Revert conflict: the generated section was edited in " + section.Path + ". Your edits were preserved; inspect the change instead.");
            changes.Add(new(section.Path, bytes, Utf8.GetBytes(text.Remove(at, section.Added.Length))));
        }
        var receiptPath = ReceiptPath(meeting); var operation = Guid.NewGuid().ToString("N");
        changes.Add(new(receiptPath, current[receiptPath], JsonSerializer.SerializeToUtf8Bytes(receipt with { State = "Reverted" })));
        Transaction.Apply(current, changes.ToArray(), "Revert AI meeting update " + receipt.Date, operation, afterWrite);
    }
}
