using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public enum OutcomeKind { Decision, Commitment, Question }
public enum OutcomeChoice { Pending, Include, Dismiss, Resolved }
public sealed record MeetingOutcome(string SegmentId, OutcomeKind Kind, double Seconds, string Quote, OutcomeChoice Choice)
{
    public string Meta => $"{TimeSpan.FromSeconds(Seconds):hh\\:mm\\:ss} · {Kind} · {Choice}";
}
public sealed record OutcomeEdit(int Schema, int Sequence, Guid Id, Guid SessionId, string TranscriptHash, DateTimeOffset CreatedUtc, string SegmentId, OutcomeKind Kind, OutcomeChoice Choice);

// Local additive review choices; candidates and drafts quote immutable transcript text.
public sealed class MeetingOutcomes
{
    public const string FileName = "meeting-outcomes.jsonl";
    private readonly string directory, hash;
    private readonly Guid session;
    private readonly Dictionary<string, TranscriptDetail> records;
    private readonly MeetingOutcome[] candidates;
    private OutcomeEdit[] edits = [];
    public bool RecoveredTail { get; private set; }
    public bool Limited { get; }
    public MeetingOutcome[] Items { get; private set; } = [];
    public MeetingOutcomes(string directory, TranscriptDetail[] source)
    {
        this.directory = LocalBackup.Root(directory); hash = MeetingArchive.TranscriptHashAt(directory);
        session = source.FirstOrDefault()?.SessionId ?? Guid.Empty;
        if (source.Any(r => r.SessionId != session) || source.Select(r => r.SegmentId).Distinct().Count() != source.Length) throw new InvalidDataException("Mixed or repeated outcome source identities.");
        records = source.ToDictionary(r => r.SegmentId);
        var found = source.Where(r => r.Text.Length <= 2000).SelectMany(r => Detect(r.Text).Select(k => Item(r, k, OutcomeChoice.Pending))).ToArray();
        Limited = found.Length > 500 || source.Any(r => r.Text.Length > 2000); candidates = found.Take(500).ToArray(); Reload();
    }
    private static IEnumerable<OutcomeKind> Detect(string text)
    {
        if (text.Contains('?')) { yield return OutcomeKind.Question; yield break; }
        bool Match(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (Match(@"\b(not|never|might|maybe|could|if|would)\b|\b(didn't|haven't|won't|cannot|can't)\b")) yield break;
        if (Match(@"\b(we agreed|we decided|decision is|agreed to|decided to)\b")) yield return OutcomeKind.Decision;
        if (Match(@"\b(will|I'll|we'll|I commit to|I promise to)\s+(send|share|review|deliver|prepare|check|confirm|follow up|schedule|complete|finish|update|provide|verify)\b")) yield return OutcomeKind.Commitment;
    }
    private static MeetingOutcome Item(TranscriptDetail r, OutcomeKind kind, OutcomeChoice choice) => new(r.SegmentId, kind, r.Start, r.Text, choice);
    private OutcomeEdit[] Read(Stream stream, out long length)
    {
        if (stream.Length > 4_000_000) throw new InvalidDataException("Outcome review journal exceeds its limit.");
        stream.Position = 0; using var copy = new MemoryStream(); stream.CopyTo(copy); var bytes = copy.ToArray(); length = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var loaded = new List<OutcomeEdit>(); var ids = new HashSet<Guid>();
        foreach (var line in Encoding.UTF8.GetString(bytes, 0, (int)length).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = JsonSerializer.Deserialize<OutcomeEdit>(line) ?? throw new InvalidDataException("Invalid outcome review.");
            if (e.Schema != 1 || e.Sequence != loaded.Count || e.Id == Guid.Empty || !ids.Add(e.Id) || e.SessionId != session || e.TranscriptHash != hash
                || !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Choice) || e.Choice == OutcomeChoice.Resolved && e.Kind != OutcomeKind.Question
                || e.SegmentId is null || !records.TryGetValue(e.SegmentId, out var source) || source.Text.Length > 2000 || loaded.Count >= 2000)
                throw new InvalidDataException("Outcome review provenance does not match this transcript.");
            loaded.Add(e);
        }
        return loaded.ToArray();
    }
    public void Reload()
    {
        VerifyOriginal(); var path = LocalBackup.Root(Path.Combine(directory, FileName));
        if (File.Exists(path)) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); edits = Read(stream, out var valid); RecoveredTail = valid != stream.Length; }
        else edits = [];
        var projected = candidates.ToDictionary(c => (c.SegmentId, c.Kind));
        foreach (var e in edits) projected[(e.SegmentId, e.Kind)] = Item(records[e.SegmentId], e.Kind, e.Choice);
        Items = projected.Values.OrderBy(i => i.Seconds).ThenBy(i => i.Kind).ToArray();
    }
    private void VerifyOriginal() { if (hash != MeetingArchive.TranscriptHashAt(directory)) throw new InvalidDataException("Original transcript changed. Reopen meeting review before continuing."); }
    public void Set(string segmentId, OutcomeKind kind, OutcomeChoice choice)
    {
        using var recordingGuard = new FileStream(LocalBackup.Root(Path.Combine(directory, "recording.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        VerifyOriginal();
        if (session == Guid.Empty || !records.TryGetValue(segmentId, out var source) || source.Text.Length > 2000 || !Enum.IsDefined(kind) || !Enum.IsDefined(choice)
            || choice == OutcomeChoice.Resolved && kind != OutcomeKind.Question) throw new InvalidDataException("Select a transcript segment up to 2,000 characters and a valid outcome choice.");
        using (var stream = new FileStream(LocalBackup.Root(Path.Combine(directory, FileName)), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            var current = Read(stream, out var valid);
            if (!current.Select(e => e.Id).SequenceEqual(edits.Select(e => e.Id))) throw new IOException("Outcome review changed in another window. Reload before saving.");
            if (current.Length >= 2000 || valid > 3_998_000) throw new InvalidDataException("Outcome review journal is full. Existing choices are safe.");
            var e = new OutcomeEdit(1, current.Length, Guid.NewGuid(), session, hash, DateTimeOffset.UtcNow, segmentId, kind, choice);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e) + "\n");
            if (valid != stream.Length) stream.SetLength(valid);
            stream.Position = valid; stream.Write(bytes); stream.Flush(true);
        }
        Reload();
    }
    public string Draft()
    {
        VerifyOriginal(); var path = LocalBackup.Root(Path.Combine(directory, FileName));
        if (!File.Exists(path) && edits.Length > 0) throw new IOException("Outcome review was removed. Reload before drafting.");
        if (File.Exists(path)) { using var stream = File.OpenRead(path); if (!Read(stream, out _).Select(e => e.Id).SequenceEqual(edits.Select(e => e.Id))) throw new IOException("Outcome review changed. Reload before drafting."); }
        var selected = Items.Where(i => i.Choice == OutcomeChoice.Include).ToArray();
        if (selected.Length == 0) return "";
        var draft = new StringBuilder("Thanks for the meeting. Here are the points I'd like to confirm from our discussion. Please correct anything I have misunderstood.\n");
        foreach (var kind in Enum.GetValues<OutcomeKind>())
        {
            var group = selected.Where(i => i.Kind == kind).ToArray(); if (group.Length == 0) continue;
            draft.Append("\n").Append(kind switch { OutcomeKind.Decision => "Decisions to confirm", OutcomeKind.Commitment => "Commitments to confirm", _ => "Questions still needing confirmation" }).Append(":\n");
            foreach (var item in group) draft.Append("- “").Append(item.Quote).Append("”\n");
        }
        return draft.ToString().TrimEnd();
    }
}
