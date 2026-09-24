using System.Text;
using System.Text.Json;

namespace SecondBrain.Core;

public sealed record SpeakerNameHint(int SchemaVersion, Guid SessionId, string SegmentId, string Binding, string Name,
    double FirstObserved, double LastObserved, string Adapter = "chrome-teams-speaking-en-v1");
public sealed record SpeakingObservation(double At, string? Name);

public static class TeamsSpeakingLabels
{
    public static bool SupportedAddress(string address) => Uri.TryCreate(address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        && uri.Host is "teams.microsoft.com" or "teams.live.com" or "teams.cloud.microsoft";
    // This adapter intentionally recognizes only explicit English speaking labels and exact roster names.
    public static string? Name(string label, IEnumerable<string> roster)
    {
        var matches = roster.Where(TranscriptReview.ValidLabel).GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).Select(g => g.Key).Where(n =>
                label.Equals(n + " is speaking", StringComparison.OrdinalIgnoreCase)
                || label.Equals(n + ", speaking", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Speaking: " + n, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

// Audio timestamps and observations share the session's monotonic clock. No cross-session identity mapping.
public sealed class SpeakerHintTimeline(Guid sessionId)
{
    private readonly object gate = new();
    private readonly List<SpeakingObservation> samples = [];
    public void Clear() { lock (gate) samples.Clear(); }
    public void Observe(double seconds, IEnumerable<string> candidates)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        var names = candidates.Where(TranscriptReview.ValidLabel).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        lock (gate)
        {
            if (samples.Count > 0 && seconds <= samples[^1].At) return;
            samples.Add(new(seconds, names.Length == 1 ? names[0] : null));
            samples.RemoveAll(s => s.At < seconds - 120);
            if (samples.Count > 300) samples.RemoveRange(0, samples.Count - 300);
        }
    }
    public SpeakerNameHint? Resolve(TranscriptDetail detail)
    {
        if (detail.SessionId != sessionId || detail.Source != AudioSource.System || detail.ConnectionId == Guid.Empty || detail.Words.Length == 0
            || detail.Words.Any(w => w.SpeakerId is null) || detail.Words.Select(w => w.SpeakerId).Distinct().Count() != 1) return null;
        var words = detail.Words.OrderBy(w => w.Start).ToArray();
        if (words.Skip(1).Where((w, i) => w.Start < words[i].End - .03).Any()) return null;
        var start = words[0].Start; var end = words.Max(w => w.End);
        SpeakingObservation[] relevant;
        lock (gate) relevant = samples.Where(s => s.At >= start - .3 && s.At <= end + .3).ToArray();
        if (relevant.Length < 2 || relevant[0].At > start + .5 || relevant[^1].At < end - .5
            || relevant[^1].At - relevant[0].At < .3 || relevant.Any(s => s.Name is null)
            || relevant.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
            || relevant.Skip(1).Where((s, i) => s.At - relevant[i].At > .8).Any()) return null;
        return new(1, sessionId, detail.SegmentId, SpeakerNameHints.Binding(detail), relevant[0].Name!, relevant[0].At, relevant[^1].At);
    }
}

// Optional provenance sidecar; no images, voiceprints or rewritten original transcript.
public sealed class SpeakerNameHints(string directory, Guid sessionId)
{
    public const string FileName = "speaker-name-hints.jsonl";
    public static string Binding(TranscriptDetail detail) => VaultIndex.Hash(JsonSerializer.Serialize(detail));
    private readonly HashSet<string> written = [];
    public void Append(TranscriptDetail detail, SpeakerNameHint hint)
    {
        if (!Valid(hint) || detail.SessionId != sessionId || hint.SessionId != sessionId || hint.SegmentId != detail.SegmentId || hint.Binding != Binding(detail))
            throw new InvalidDataException("Speaker hint provenance does not match the transcript.");
        if (written.Contains(hint.SegmentId)) return;
        var path = LocalBackup.Root(Path.Combine(directory, FileName));
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new IOException("Speaker hint size limit reached.");
        if (stream.Length > 0) { stream.Position = stream.Length - 1; if (stream.ReadByte() != '\n') throw new InvalidDataException("Speaker hint tail is incomplete."); }
        stream.Position = stream.Length; stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hint) + "\n")); stream.Flush(true); written.Add(hint.SegmentId);
    }
    private static bool Valid(SpeakerNameHint hint) => hint.SchemaVersion == 1 && hint.SessionId != Guid.Empty
        && !string.IsNullOrWhiteSpace(hint.SegmentId) && hint.SegmentId.Length <= 256 && hint.Binding?.Length == 64
        && TranscriptReview.ValidLabel(hint.Name) && double.IsFinite(hint.FirstObserved) && double.IsFinite(hint.LastObserved)
        && hint.FirstObserved >= 0 && hint.LastObserved >= hint.FirstObserved && hint.Adapter == "chrome-teams-speaking-en-v1";
    public static Dictionary<string, string> Read(string directory, TranscriptDetail[] records, out string warning)
    {
        warning = ""; var result = new Dictionary<string, string>(); var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return result;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException();
            var bytes = new byte[stream.Length]; stream.ReadExactly(bytes); var length = bytes.Length;
            while (length > 0 && bytes[length - 1] != '\n') length--;
            if (length != bytes.Length) warning = "Incomplete final speaker hint ignored.";
            var originals = records.ToDictionary(r => r.SegmentId);
            var seen = new Dictionary<string, SpeakerNameHint>();
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, length).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var hint = JsonSerializer.Deserialize<SpeakerNameHint>(line) ?? throw new InvalidDataException();
                if (!Valid(hint) || !originals.TryGetValue(hint.SegmentId, out var record) || record.Source != AudioSource.System
                    || record.SessionId != hint.SessionId || Binding(record) != hint.Binding || seen.TryGetValue(hint.SegmentId, out var prior) && prior != hint)
                    throw new InvalidDataException();
                seen[hint.SegmentId] = hint;
                foreach (var word in record.Words) result[word.Id] = hint.Name + " (screen hint)";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { warning = "Speaker hints unavailable or mismatched; original audio labels retained."; result.Clear(); }
        return result;
    }
}
