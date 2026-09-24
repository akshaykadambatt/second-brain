using System.Text;
using System.Text.Json;

namespace SecondBrain.Core;

public sealed record ReviewWord(string Id, string SegmentId, AudioSource Source, double Start, double End, string Text,
    string? SpeakerId, string Speaker, bool Timed, bool Corrected = false);
public sealed record ReviewTurn(ReviewWord[] Words, bool Bookmarked)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
    public string Meta => $"{(Bookmarked ? "★ · " : "")}{Words[0].Speaker} · {Words[0].Source} · {TimeSpan.FromSeconds(Words[0].Start):hh\\:mm\\:ss}"
        + (Words.Any(w => w.Corrected) ? " · corrected" : "");
}
public sealed record ReviewEdit(int Version, int Sequence, Guid Id, Guid SessionId, DateTimeOffset CreatedUtc,
    string Action, string[] WordIds, string Binding, string? SpeakerId = null, string? Label = null, Guid? UndoId = null);

// Additive local journal; transcript/word sidecars are never rewritten. Each
// edit binds to immutable source tokens, and stale editors must reload.
public sealed class TranscriptReview
{
    public const string FileName = "transcript-review.jsonl";
    private readonly string directory;
    private readonly ReviewWord[] originals;
    private readonly Dictionary<string, ReviewWord> byId;
    private readonly Guid sessionId;
    private ReviewEdit[] edits = [];
    public ReviewWord[] Words { get; private set; } = [];
    public HashSet<string> Bookmarks { get; private set; } = [];
    public int Revision => edits.Length;
    public bool RecoveredTail { get; private set; }
    public bool CanUndo => ActiveEdits().Any();
    public TranscriptReview(string directory, TranscriptDetail[] records, bool loadEdits = true)
    {
        this.directory = directory;
        sessionId = records.FirstOrDefault()?.SessionId ?? Guid.Empty;
        if (records.Any(r => r.SessionId != sessionId)) throw new InvalidDataException("Transcript mixes recording sessions.");
        originals = records.SelectMany(r => r.Words.Length == 0
            ? new[] { new ReviewWord(r.SegmentId + ":segment", r.SegmentId, r.Source, r.Start, r.End, r.Text, null, "Unknown", false) }
            : r.Words.Select(w => new ReviewWord(w.Id, r.SegmentId, r.Source, w.Start, w.End, w.Text, w.SpeakerId, w.SpeakerLabel ?? "Unknown", true)))
            .OrderBy(w => w.Start).ThenBy(w => w.Source).ToArray();
        byId = originals.ToDictionary(w => w.Id);
        if (loadEdits) Reload(); else Project();
    }
    private string Binding(IEnumerable<string> ids) => VaultIndex.Hash(JsonSerializer.Serialize(ids.Select(id => byId[id]).ToArray()));
    private ReviewEdit[] Read(Stream stream, out long validLength)
    {
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Review journal exceeds its size limit.");
        stream.Position = 0; using var bytes = new MemoryStream(); stream.CopyTo(bytes); var raw = bytes.ToArray();
        var end = Array.LastIndexOf(raw, (byte)'\n') + 1; validLength = end;
        var result = new List<ReviewEdit>(); var known = new HashSet<Guid>(); var undone = new HashSet<Guid>();
        foreach (var line in Encoding.UTF8.GetString(raw, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = JsonSerializer.Deserialize<ReviewEdit>(line) ?? throw new InvalidDataException("Invalid review record.");
            if (e.Version != 1 || e.Sequence != result.Count || e.SessionId != sessionId || e.Id == Guid.Empty || !known.Add(e.Id)
                || e.WordIds is null || e.WordIds.Length > 100000 || e.WordIds.Distinct().Count() != e.WordIds.Length
                || e.WordIds.Any(id => id is null || !byId.ContainsKey(id)) || e.Binding != Binding(e.WordIds))
                throw new InvalidDataException("Review provenance does not match the transcript. Original text is unchanged.");
            if (e.Action == "undo")
            {
                if (e.WordIds.Length != 0 || e.UndoId is not { } target || !result.Any(x => x.Id == target && x.Action != "undo") || !undone.Add(target))
                    throw new InvalidDataException("Invalid review undo.");
            }
            else if (e.Action == "speaker")
            {
                if (e.WordIds.Length == 0 || string.IsNullOrWhiteSpace(e.SpeakerId) || e.SpeakerId.Length > 200 || !ValidLabel(e.Label))
                    throw new InvalidDataException("Invalid speaker correction.");
            }
            else if (e.Action is not ("bookmark" or "unbookmark") || e.WordIds.Length != 1) throw new InvalidDataException("Invalid review action.");
            result.Add(e);
        }
        return result.ToArray();
    }
    public void Reload()
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            edits = Read(stream, out var length); RecoveredTail = length != stream.Length;
        }
        else edits = [];
        Project();
    }
    private IEnumerable<ReviewEdit> ActiveEdits()
    {
        var undone = edits.Where(e => e.Action == "undo").Select(e => e.UndoId).ToHashSet();
        return edits.Where(e => e.Action != "undo" && !undone.Contains(e.Id));
    }
    private void Project()
    {
        var changes = new Dictionary<string, (string Id, string Label)>(); Bookmarks = [];
        foreach (var e in ActiveEdits())
        {
            if (e.Action == "speaker") foreach (var id in e.WordIds) changes[id] = (e.SpeakerId!, e.Label!);
            else if (e.Action == "bookmark") Bookmarks.Add(e.WordIds[0]);
            else Bookmarks.Remove(e.WordIds[0]);
        }
        Words = originals.Select(w => changes.TryGetValue(w.Id, out var c) ? w with { SpeakerId = c.Id, Speaker = c.Label, Corrected = true } : w).ToArray();
    }
    public ReviewTurn[] Turns(string search = "", bool bookmarksOnly = false)
    {
        var turns = new List<ReviewTurn>(); var group = new List<ReviewWord>();
        foreach (var word in Words)
        {
            if (group.Count > 0 && (group[^1].Source != word.Source || group[^1].SpeakerId != word.SpeakerId || group[^1].Speaker != word.Speaker
                || word.Start - group[^1].End > 2 || word.SpeakerId is null && group[^1].SegmentId != word.SegmentId)) Flush();
            group.Add(word);
        }
        Flush();
        return turns.Where(t => (!bookmarksOnly || t.Bookmarked) && (t.Text.Contains(search, StringComparison.OrdinalIgnoreCase)
            || t.Words[0].Speaker.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        void Flush() { if (group.Count == 0) return; turns.Add(new(group.ToArray(), group.Any(w => Bookmarks.Contains(w.Id)))); group.Clear(); }
    }
    public static bool ValidLabel(string? label) => !string.IsNullOrWhiteSpace(label) && label.Length <= 100 && !label.Any(char.IsControl);
    private void Append(string action, string[] ids, string? speaker = null, string? label = null, Guid? undo = null)
    {
        if (sessionId == Guid.Empty || ids.Any(id => !byId.ContainsKey(id))) throw new InvalidDataException("Select words from this meeting first.");
        var path = LocalBackup.Root(Path.Combine(directory, FileName));
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var current = Read(stream, out var valid);
        if (current.Length != edits.Length || !current.Select(e => e.Id).SequenceEqual(edits.Select(e => e.Id))) throw new IOException("Review changed in another window. Reload before saving.");
        var edit = new ReviewEdit(1, edits.Length, Guid.NewGuid(), sessionId, DateTimeOffset.UtcNow, action, ids, Binding(ids), speaker, label, undo);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(edit) + "\n");
        if (line.Length > 1024 * 1024 || valid + line.Length > 16 * 1024 * 1024) throw new InvalidDataException("Review journal is full; prior corrections are safe.");
        if (valid != stream.Length) stream.SetLength(valid);
        stream.Position = valid; stream.Write(line); stream.Flush(true);
        edits = [.. edits, edit]; RecoveredTail = false; Project();
    }
    public void Assign(IEnumerable<string> wordIds, string label, string? speakerId = null)
    {
        if (!ValidLabel(label)) throw new InvalidDataException("Enter a speaker name of 1–100 characters.");
        if (speakerId is not null && (string.IsNullOrWhiteSpace(speakerId) || speakerId.Length > 200)) throw new InvalidDataException("Invalid speaker identity.");
        var ids = wordIds.Distinct().ToArray();
        if (ids.Length == 0 || ids.Length > 100000) throw new InvalidDataException("Select a turn or words to correct.");
        Append("speaker", ids, speakerId ?? sessionId + ":manual:" + Guid.NewGuid().ToString("N"), label.Trim());
    }
    public void Rename(string speakerId, string label) => Assign(Words.Where(w => w.SpeakerId == speakerId).Select(w => w.Id), label, speakerId);
    public void Merge(string from, string into)
    {
        if (from == into) throw new InvalidDataException("Choose a different destination speaker.");
        var target = Words.FirstOrDefault(w => w.SpeakerId == into) ?? throw new InvalidDataException("Destination speaker not found.");
        Assign(Words.Where(w => w.SpeakerId == from).Select(w => w.Id), target.Speaker, into);
    }
    public void Bookmark(string id) => Append(Bookmarks.Contains(id) ? "unbookmark" : "bookmark", [id]);
    public void Undo()
    {
        var last = ActiveEdits().LastOrDefault() ?? throw new InvalidOperationException("No correction to undo.");
        Append("undo", [], undo: last.Id);
    }
}
