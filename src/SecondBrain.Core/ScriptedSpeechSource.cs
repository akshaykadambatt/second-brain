namespace SecondBrain.Core;

public sealed class ScriptedSpeechSource
{
    public sealed record Entry(double At, SpeechSegment Segment, string Label);
    private readonly DeepgramProgress progress;
    private readonly Action<SpeechSegment>? observe;
    private int next;
    private double elapsed;
    public IReadOnlyList<Entry> Events { get; }
    public bool Running { get; private set; } = true;
    public event Action<string, string>? Heard;

    public ScriptedSpeechSource(ReaderSession session, Action<SpeechSegment>? observe = null)
    {
        if (session.Words.Count < 15) throw new InvalidOperationException("Use at least 15 words for the speech replay.");
        progress = new DeepgramProgress(session);
        this.observe = observe;
        string Phrase(int start, int count, int omit = -1) => string.Join(" ", session.Words.Skip(start).Take(count).Where((_, i) => i != omit).Select(w => w.Text));
        Events = [
            new(.3, new(0, .5, Phrase(0, 3), false, false, .95f), "Provisional words"),
            new(.8, new(0, 1, Phrase(0, 5), false, false, .95f), "Revised provisional words"),
            new(1.2, new(0, 1.2, Phrase(0, 5), true, true, .95f), "Confirmed phrase; pause follows"),
            new(1.5, new(0, 1.2, Phrase(0, 5), true, true, .95f), "Repeated final result (should not advance twice)"),
            new(3.6, new(1.2, 1.5, Phrase(5, 5, 1), true, true, .95f), "Delayed phrase with one skipped word"),
            new(4.0, new(2.7, .5, "unrelated uncertain speech", true, true, .05f), "Uncertain speech (hold position)"),
            new(5.4, new(3.2, 1.4, Phrase(10, 5), true, true, .95f), "Return to script")
        ];
    }
    public void Stop() => Running = false;
    public void Tick(double seconds)
    {
        if (!Running || seconds <= 0 || !double.IsFinite(seconds)) return;
        elapsed += Math.Min(seconds, 1d / 30);
        while (next < Events.Count && Events[next].At <= elapsed)
        {
            var item = Events[next++];
            if (observe is null) progress.Observe(item.Segment); else observe(item.Segment);
            Heard?.Invoke(item.Segment.Text, item.Label);
        }
        if (elapsed >= 7) Running = false;
    }
}
