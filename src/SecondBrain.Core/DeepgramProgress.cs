using System.Text.Json;

namespace SecondBrain.Core;

public sealed record SpeechSegment(double Start, double Duration, string Text, bool Final, bool SpeechFinal, float Confidence);

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
        return new(root.GetProperty("start").GetDouble(), root.GetProperty("duration").GetDouble(),
            alternative.GetProperty("transcript").GetString() ?? "", root.GetProperty("is_final").GetBoolean(),
            root.TryGetProperty("speech_final", out var end) && end.GetBoolean(), alternative.GetProperty("confidence").GetSingle());
    }
}

// Deepgram finalizes chunks within an utterance. Revisions share a start timestamp;
// they must keep their anchor, while each new chunk starts at the current word.
public sealed class DeepgramProgress(ReaderSession session)
{
    private readonly SpeechFollower follower = new(session);
    private double currentStart = -1, finalizedThrough = -1;
    private bool suppressUntilFinal;

    public void Reanchor() { follower.BeginUtterance(); suppressUntilFinal = true; }

    public bool Observe(SpeechSegment segment)
    {
        if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.Duration) || segment.Duration < 0) return false;
        if (segment.Start < currentStart || segment.Start < finalizedThrough - .001) return false;
        if (segment.Start != currentStart) { currentStart = segment.Start; follower.BeginUtterance(); }
        var moved = !suppressUntilFinal && !string.IsNullOrWhiteSpace(segment.Text)
            && follower.Observe(segment.Text, segment.Final, segment.Confidence);
        if (segment.Final)
        {
            finalizedThrough = Math.Max(finalizedThrough, segment.Start + Math.Max(segment.Duration, .001));
            suppressUntilFinal = false;
        }
        return moved;
    }
}
