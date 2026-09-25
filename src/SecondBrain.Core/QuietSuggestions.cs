namespace SecondBrain.Core;

public sealed record QuietSuggestion(Guid Topic, string Text, string Source, double ExpiresAt);
public sealed class QuietSuggestions
{
    private readonly Queue<(Guid Topic, string Key)> shown = new();
    private Guid topic;
    private double lastShown = double.NegativeInfinity;
    private bool enabled;
    public bool Enabled { get => enabled; set { enabled = value; if (!value) Current = null; } }
    public QuietSuggestion? Current { get; private set; }
    public void Topic(Guid next) { if (topic != next) { topic = next; Current = null; } }
    public void Tick(double now) { if (Current is { } current && now >= current.ExpiresAt) Current = null; }
    public bool Offer(Guid next, string text, string source, double now)
    {
        Topic(next); Tick(now); var key = QuestionGate.Normalize(text + " " + source);
        if (!enabled || !double.IsFinite(now) || next == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Length > 500 || source.Length > 1000
            || Current is not null || now - lastShown < 30 || shown.Any(s => s.Topic == next || s.Key == key)) return false;
        Current = new(next, text, source, now + 30); lastShown = now; shown.Enqueue((next, key)); while (shown.Count > 64) shown.Dequeue(); return true;
    }
    public void Dismiss() => Current = null;
    public void EndSession() { topic = Guid.Empty; Current = null; shown.Clear(); }
}
