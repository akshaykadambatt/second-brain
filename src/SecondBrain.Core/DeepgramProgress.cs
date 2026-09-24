using System.Text.Json;

namespace SecondBrain.Core;

public sealed record SpeechSegment(double Start, double Duration, string Text, bool Final, bool SpeechFinal, float Confidence)
{
    public double? LastWordEnd { get; init; }
    public SpeechWord[] Words { get; init; } = [];
    public string WordTimingStatus { get; init; } = "Provider omitted word timing";
}

public static class DeepgramProtocol
{
    public static SpeechSegment? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();
        if (type == "Error") throw new InvalidOperationException("Deepgram reported a transcription error. Stop and retry the connection.");
        if (type != "Results") return null;
        var alternative = root.GetProperty("channel").GetProperty("alternatives")[0];
        var start = root.GetProperty("start").GetDouble(); var duration = root.GetProperty("duration").GetDouble();
        var parsed = new List<SpeechWord>(); var supplied = 0;
        if (alternative.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            supplied = words.GetArrayLength();
            foreach (var word in words.EnumerateArray().Take(4000))
            {
                if (word.ValueKind != JsonValueKind.Object || !word.TryGetProperty("start", out var ws) || ws.ValueKind != JsonValueKind.Number || !ws.TryGetDouble(out var begins)
                    || !word.TryGetProperty("end", out var we) || we.ValueKind != JsonValueKind.Number || !we.TryGetDouble(out var ends) || !double.IsFinite(begins) || !double.IsFinite(ends)
                    || begins < start || ends < begins || ends > start + duration + .001) continue;
                var text = word.TryGetProperty("punctuated_word", out var punctuated) && punctuated.ValueKind == JsonValueKind.String ? punctuated.GetString()
                    : word.TryGetProperty("word", out var plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString() : null;
                if (string.IsNullOrWhiteSpace(text) || text.Length > 1000) continue;
                float? Probability(string name) => word.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number)
                    && float.IsFinite(number) && number >= 0 && number <= 1 ? number : null;
                int? speaker = word.TryGetProperty("speaker", out var label) && label.ValueKind == JsonValueKind.Number && label.TryGetInt32(out var number) && number >= 0 && number <= 10000 ? number : null;
                parsed.Add(new(text, begins, ends, Probability("confidence"), speaker, Probability("speaker_confidence")));
            }
        }
        return new(start, duration,
            alternative.GetProperty("transcript").GetString() ?? "", root.GetProperty("is_final").GetBoolean(),
            root.TryGetProperty("speech_final", out var end) && end.GetBoolean(), alternative.GetProperty("confidence").GetSingle())
        { LastWordEnd = parsed.LastOrDefault()?.End, Words = parsed.ToArray(), WordTimingStatus = supplied == 0 ? "Provider omitted word timing"
            : parsed.Count != supplied ? "Some invalid word timing was omitted" : "Word timing available" };
    }
}

// Deepgram finalizes chunks within an utterance. Revisions share a start timestamp;
// they must keep their anchor, while each new chunk starts at the current word.
public sealed class DeepgramProgress(ReaderSession session)
{
    private readonly SpeechFollower follower = new(session);
    private double currentStart = -1, finalizedThrough = -1;
    private bool suppressUntilFinal;
    public SpeechMatch? LastMatch { get; private set; }
    public bool Ignored { get; private set; }
    public string Reason => follower.Reason;

    public void Reanchor() { follower.BeginUtterance(); suppressUntilFinal = true; }

    public bool Observe(SpeechSegment segment, AudioSource source = AudioSource.Microphone)
    {
        LastMatch = null; Ignored = true;
        if (source != AudioSource.Microphone || !double.IsFinite(segment.Start) || segment.Start < 0 || !double.IsFinite(segment.Duration) || segment.Duration < 0) return false;
        if (segment.Start < currentStart || segment.Start < finalizedThrough - .001) return false;
        Ignored = false;
        if (segment.Start != currentStart) { currentStart = segment.Start; follower.BeginUtterance(); }
        var moved = !suppressUntilFinal && !string.IsNullOrWhiteSpace(segment.Text)
            && follower.Observe(segment.Text, segment.Final, segment.Confidence);
        if (!suppressUntilFinal && !string.IsNullOrWhiteSpace(segment.Text)) LastMatch = follower.LastMatch;
        if (segment.Final)
        {
            finalizedThrough = Math.Max(finalizedThrough, segment.Start + Math.Max(segment.Duration, .001));
            suppressUntilFinal = false;
        }
        return moved;
    }
}
