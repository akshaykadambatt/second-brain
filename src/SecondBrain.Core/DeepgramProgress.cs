using System.Text.Json;

namespace SecondBrain.Core;

public sealed record SpeechSegment(double Start, double Duration, string Text, bool Final, bool SpeechFinal, float Confidence)
{
    public double? LastWordEnd { get; init; }
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
        double? lastWordEnd = null;
        if (alternative.TryGetProperty("words", out var words) && words.GetArrayLength() > 0)
            lastWordEnd = words[words.GetArrayLength() - 1].GetProperty("end").GetDouble();
        return new(root.GetProperty("start").GetDouble(), root.GetProperty("duration").GetDouble(),
            alternative.GetProperty("transcript").GetString() ?? "", root.GetProperty("is_final").GetBoolean(),
            root.TryGetProperty("speech_final", out var end) && end.GetBoolean(), alternative.GetProperty("confidence").GetSingle()) { LastWordEnd = lastWordEnd };
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
