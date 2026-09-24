namespace SecondBrain.Core;

// Final transcript segments can split one question. Wait for the speech endpoint
// (or a quiet fallback) before asking, while provisional text remains UI-only.
public sealed class QuestionUtterance
{
    private string text = "";
    private double lastActivity;
    public string? Observe(SpeechSegment segment, double now)
    {
        if (!string.IsNullOrWhiteSpace(segment.Text)) lastActivity = now;
        if (segment.Final && !string.IsNullOrWhiteSpace(segment.Text))
            text = (text + " " + segment.Text).Trim();
        if (text.Length > 2000) { text = ""; return null; }
        return segment.Final && segment.SpeechFinal ? Take() : null;
    }
    public string? Flush(double now) => now - lastActivity >= 1 ? Take() : null;
    public void Reset() => text = "";
    private string? Take() { var result = text; text = ""; return result.Length == 0 ? null : result; }
}
