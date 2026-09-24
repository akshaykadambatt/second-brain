using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondBrain.Core;

public sealed record SpeechWord(string Text, double Start, double End, float? Confidence = null, int? Speaker = null, float? SpeakerConfidence = null);
public sealed record TranscriptWord(string Id, string Text, double Start, double End, float? Confidence = null,
    int? ProviderSpeaker = null, float? SpeakerConfidence = null, string? SpeakerId = null, string? SpeakerLabel = null);
public sealed record TranscriptDetail(int SchemaVersion, Guid SessionId, string SegmentId, AudioSource Source, Guid ConnectionId,
    double Start, double End, string Text, TranscriptWord[] Words, string MetadataStatus = "Word timing available");

// Optional append-only sidecar. The original transcript journal remains authoritative.
public sealed class TranscriptDetails : IDisposable
{
    public const string FileName = "transcript-details.jsonl";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly FileStream stream;
    private readonly object gate = new();
    private readonly Dictionary<string, string> records = [];
    private readonly Guid sessionId;
    private bool failed;
    public TranscriptDetails(string directory, Guid sessionId)
    {
        this.sessionId = sessionId;
        stream = new(Path.Combine(directory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n')
                {
                    var position = stream.Length - 1;
                    while (position >= 0) { stream.Position = position; if (stream.ReadByte() == '\n') break; position--; }
                    stream.SetLength(position + 1); stream.Flush(true);
                }
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                while (reader.ReadLine() is { } line)
                {
                    var item = JsonSerializer.Deserialize<TranscriptDetail>(line, Json) ?? throw new InvalidDataException("Invalid word-detail record.");
                    Validate(item, sessionId); var canonical = JsonSerializer.Serialize(item, Json);
                    if (records.TryGetValue(item.SegmentId, out var prior) && prior != canonical) throw new InvalidDataException("Conflicting word-detail records.");
                    records[item.SegmentId] = canonical;
                }
            }
            stream.Position = stream.Length;
        }
        catch { stream.Dispose(); throw; }
    }
    private static void Validate(TranscriptDetail item, Guid sessionId)
    {
        if (item.SchemaVersion != 1 || item.SessionId != sessionId || item.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(item.SegmentId)
            || item.SegmentId.Length > 256 || !Enum.IsDefined(item.Source) || !double.IsFinite(item.Start) || !double.IsFinite(item.End)
            || item.Start < 0 || item.End < item.Start || item.Text is null || item.Text.Length > 32000 || item.Words is null || item.Words.Length > 4000
            || item.MetadataStatus is null || item.MetadataStatus.Length > 200) throw new InvalidDataException("Invalid transcript detail provenance.");
        var ids = new HashSet<string>();
        foreach (var word in item.Words)
            if (word is null || string.IsNullOrWhiteSpace(word.Id) || word.Id.Length > 280 || !ids.Add(word.Id) || word.Text is null || word.Text.Length > 1000
                || !double.IsFinite(word.Start) || !double.IsFinite(word.End) || word.Start < item.Start - .001 || word.End > item.End + .001 || word.End < word.Start
                || word.Confidence is { } c && (!float.IsFinite(c) || c < 0 || c > 1)
                || word.SpeakerConfidence is { } sc && (!float.IsFinite(sc) || sc < 0 || sc > 1)
                || word.ProviderSpeaker is < 0 or > 10000 || word.SpeakerId?.Length > 200 || word.SpeakerLabel?.Length > 120)
                throw new InvalidDataException("Invalid transcript word metadata.");
    }
    public bool Append(TranscriptDetail item)
    {
        Validate(item, sessionId); var line = JsonSerializer.Serialize(item, Json);
        lock (gate)
        {
            if (failed) throw new IOException("Word-detail writer previously failed.");
            if (records.TryGetValue(item.SegmentId, out var prior))
            { if (prior != line) throw new InvalidDataException("A stable segment ID has conflicting word details."); return false; }
            try { stream.Write(Encoding.UTF8.GetBytes(line + "\n")); stream.Flush(true); }
            catch { failed = true; throw; }
            records.Add(item.SegmentId, line); return true;
        }
    }
    public static TranscriptDetail[] Read(string directory)
    {
        var originals = TranscriptLog.Read(directory).Where(e => e.Kind == "Final" && e.Source is not null).ToArray();
        var details = new Dictionary<string, TranscriptDetail>(); var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var value = JsonSerializer.Deserialize<TranscriptDetail>(line, Json) ?? throw new InvalidDataException("Invalid word-detail record.");
                Validate(value, value.SessionId);
                if (!details.TryAdd(value.SegmentId, value)) throw new InvalidDataException("Duplicate word-detail identity.");
            }
        }
        return originals.Select((e, i) =>
        {
            var id = e.Id ?? "legacy-" + VaultIndex.Hash(JsonSerializer.Serialize(e, Json) + ":" + i);
            if (details.TryGetValue(id, out var found))
            {
                if (found.SessionId != e.SessionId || found.Source != e.Source || found.ConnectionId != e.ConnectionId || found.Start != e.Start || found.End != e.End || found.Text != e.Text)
                    throw new InvalidDataException("Word details do not match their original transcript segment.");
                return found;
            }
            return new TranscriptDetail(1, e.SessionId, id, e.Source!.Value, e.ConnectionId, e.Start, e.End, e.Text, [], "Word timing unavailable for this segment");
        }).ToArray();
    }
    public static TranscriptDetail From(TranscriptEntry entry, SpeechWord[] words, string status)
    {
        if (entry.Id is null || entry.Source is null) throw new InvalidDataException("Word details require a stable source segment.");
        return new(1, entry.SessionId, entry.Id, entry.Source.Value, entry.ConnectionId, entry.Start, entry.End, entry.Text,
            words.Select((w, i) => new TranscriptWord(entry.Id + ":w" + i, w.Text, w.Start, w.End, w.Confidence, w.Speaker, w.SpeakerConfidence)).ToArray(), status);
    }
    public void Dispose() { lock (gate) stream.Dispose(); }
}
