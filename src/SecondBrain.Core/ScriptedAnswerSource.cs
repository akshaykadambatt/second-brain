namespace SecondBrain.Core;

// Deterministic provider fixture: deliberate gaps, reordered chunks, duplicates and interruption.
public sealed class ScriptedAnswerSource
{
    private readonly AnswerInbox inbox;
    private readonly List<(double At, AnswerEvent Event)> events = [];
    private double elapsed;
    public StreamAnswer First { get; }
    public bool Running => events.Count > 0;
    public ScriptedAnswerSource(AnswerInbox inbox, int run)
    {
        this.inbox = inbox;
        var request = Guid.NewGuid();
        First = inbox.Begin(request, Guid.NewGuid(), $"{run}. Quick answer");
        var deeper = inbox.Begin(request, Guid.NewGuid(), $"{run}. Deeper answer", First.Id);
        var other = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), $"{run}. Next question · interrupted");
        void Add(double at, StreamAnswer answer, int sequence, string text = "", AnswerEventKind kind = AnswerEventKind.Delta)
            => events.Add((at, new(answer.RequestId, answer.Id, sequence, text, kind)));
        Add(.1, First, 0, "We can start with a small ");
        Add(.8, First, 1, "working reader and improve it together.\n\n");
        Add(1.1, First, 1, "working reader and improve it together.\n\n");
        Add(4, deeper, 0, "A useful next step is to measure how the text behaves during interruptions. ");
        Add(5, deeper, 1, "The reader should hold your place while another answer arrives.\n\n", AnswerEventKind.Complete);
        Add(6, other, 0, "This answer belongs to a different question and waits in the queue.\n\nThe next paragraph is unfinished");
        Add(7, other, 1, "", AnswerEventKind.Interrupted);
        Add(8, First, 3, "The next paragraph arrives only when it is ready to read.\n\n");
        Add(9, First, 2, "If you reach the end, take a breath. ");
        Add(12, First, 4, "New questions stay in the queue. Choose an answer when you are ready, and return here without losing your place.\n\n");
        Add(14, First, 5, "This demonstration is complete. Your own script will return when you close the demo.", AnswerEventKind.Complete);
    }
    public void Tick(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        elapsed += Math.Min(seconds, .1);
        foreach (var item in events.Where(e => e.At <= elapsed).ToArray())
        { inbox.Accept(item.Event); events.Remove(item); }
    }
    public void Stop()
    {
        foreach (var id in events.Select(e => e.Event.RequestId).Distinct().ToArray()) inbox.Supersede(id);
        events.Clear();
    }
}
