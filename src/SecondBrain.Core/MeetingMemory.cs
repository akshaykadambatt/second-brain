using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record MemoryExcerpt(string Id, AudioSource? Source, double Start, double End, string Text, int Priority);
public sealed record MeetingMemoryReport(int Schema, Guid SessionId, string TranscriptHash, MemoryExcerpt[] Excerpts, bool Omitted);

// Bounded extractive memory: source wording stays intact, and observations never become confirmed facts.
public sealed class MeetingMemory
{
    private readonly List<MemoryExcerpt> excerpts = [];
    public IReadOnlyList<MemoryExcerpt> Excerpts => excerpts;
    public bool Omitted { get; private set; }
    public void Clear() { excerpts.Clear(); Omitted = false; }
    public void Observe(TranscriptEntry entry)
    {
        if (entry.Kind != "Final" || string.IsNullOrWhiteSpace(entry.Text)) return;
        var id = entry.Id ?? VaultIndex.Hash($"{entry.Source}|{entry.Start:R}|{entry.End:R}|{entry.Text}");
        if (excerpts.Any(e => e.Id == id)) return;
        var priority = Regex.IsMatch(entry.Text, @"\b(agreed|decided|decision|commitment|will|must|deadline|owner|risk|blocked|budget|follow up|action|requirement)\b", RegexOptions.IgnoreCase) ? 2 : 1;
        // Keep complete leading sentences when possible; explicitly mark a shortened source segment.
        var text = entry.Text.Trim();
        if (text.Length > 800)
        {
            var end = text.LastIndexOfAny(['.', '?', '!'], 799);
            text = text[..(end > 100 ? end + 1 : 760)] + " [source excerpt shortened]"; Omitted = true;
        }
        excerpts.Add(new(id, entry.Source, entry.Start, entry.End, text, priority));
        while (excerpts.Count > 40 || excerpts.Sum(e => e.Text.Length + e.Id.Length + 80) > 8000 || excerpts.Count(e => e.Priority == 1) > 8)
        {
            var remove = excerpts.FindIndex(e => e.Priority == 1); if (remove < 0) remove = 0;
            excerpts.RemoveAt(remove); Omitted = true;
        }
    }
    public string Snapshot(int maximumCharacters = 8500)
    {
        if (excerpts.Count == 0 || maximumCharacters < 250) return "";
        var text = new System.Text.StringBuilder("[Earlier meeting excerpts. Selective source wording, not confirmed current facts; other discussion is omitted.]\n");
        foreach (var e in excerpts.OrderByDescending(e => e.Priority).ThenBy(e => e.Start))
        {
            var line = $"[source={e.Id}; {e.Start:F2}–{e.End:F2}s; {e.Source}] {e.Text}\n";
            if (text.Length + line.Length <= maximumCharacters) text.Append(line);
        }
        return text.ToString();
    }
    public static MeetingMemoryReport Build(string directory)
    {
        var manifest = RecordingSession.ReadManifest(directory); var path = Path.Combine(directory, "transcript.jsonl");
        if (new FileInfo(path).Length > 40_000_000) throw new InvalidDataException("Transcript exceeds the memory rebuild limit.");
        var original = File.ReadAllText(path); var memory = new MeetingMemory();
        foreach (var entry in TranscriptLog.Read(directory))
        {
            if (entry.SessionId != manifest.Id) throw new InvalidDataException("Memory source has a different session identity.");
            memory.Observe(entry);
        }
        if (File.ReadAllText(path) != original) throw new IOException("Transcript changed during memory rebuild; retry after stopping.");
        return new(1, manifest.Id, VaultIndex.Hash(original), memory.Excerpts.ToArray(), memory.Omitted);
    }
    public static void Save(string directory)
    {
        var report = Build(directory);
        VaultTransaction.SaveJson(Path.Combine(directory, "meeting-memory.json"), report);
    }
    public static MeetingMemoryReport? Read(string directory)
    {
        var path = Path.Combine(directory, "meeting-memory.json"); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 100_000) throw new InvalidDataException("Memory sidecar exceeds its limit.");
        var report = JsonSerializer.Deserialize<MeetingMemoryReport>(File.ReadAllText(path));
        if (report is not { Schema: 1, Excerpts.Length: <= 40 } || report.SessionId != RecordingSession.ReadManifest(directory).Id
            || report.TranscriptHash != VaultIndex.Hash(File.ReadAllText(Path.Combine(directory, "transcript.jsonl")))) throw new InvalidDataException("Memory source changed; rebuild from the original transcript.");
        return report;
    }
}
