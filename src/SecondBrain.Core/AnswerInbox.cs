using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public enum AnswerState { Receiving, Complete, Interrupted, Superseded, Failed }
public enum AnswerEventKind { Delta, Complete, Interrupted }
public sealed record AnswerEvent(Guid RequestId, Guid AnswerId, int Sequence, string Text = "", AnswerEventKind Kind = AnswerEventKind.Delta);
public sealed record AnswerBlock(Guid Id, string Text, IReadOnlyList<ScriptWord> Words);

// One UI-thread-owned inbox shared by dummy sources and future provider adapters.
public sealed class StreamAnswer
{
    private readonly List<AnswerBlock> blocks = [];
    internal readonly SortedDictionary<int, AnswerEvent> Pending = [];
    internal readonly Dictionary<int, AnswerEvent> Seen = [];
    internal string Buffer = "";
    internal int NextSequence, ReceivedCharacters;
    public Guid RequestId { get; }
    public Guid Id { get; }
    public Guid? ParentAnswerId { get; }
    public string Title { get; }
    public AnswerState State { get; internal set; } = AnswerState.Receiving;
    public string Detail { get; internal set; } = "Waiting for the first complete paragraph";
    public IReadOnlyList<AnswerBlock> Blocks => blocks.AsReadOnly();
    public int WordCount { get; private set; }
    public int SavedPosition { get; set; }
    internal StreamAnswer(Guid request, Guid id, string title, Guid? parent)
    { RequestId = request; Id = id; Title = title; ParentAnswerId = parent; }
    internal void Commit(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var id = Guid.NewGuid(); var matches = Regex.Matches(text, @"\S+");
        var start = WordCount;
        var words = matches.Select((m, i) => new ScriptWord(start + i, m.Value,
            i + 1 < matches.Count ? text[(m.Index + m.Length)..matches[i + 1].Index] : "\n\n", id)).ToArray();
        blocks.Add(new(id, text, Array.AsReadOnly(words))); WordCount += words.Length;
    }
}

public sealed class AnswerInbox
{
    public const int Capacity = 100;
    public const int MaximumBlocks = 128;
    public const int MaximumParagraphCharacters = 1500;
    private readonly List<StreamAnswer> answers = [];
    public IReadOnlyList<StreamAnswer> Answers => answers.AsReadOnly();
    public event Action<StreamAnswer>? Changed;
    public bool Resume(Guid requestId, Guid answerId)
    {
        var answer = answers.FirstOrDefault(a => a.Id == answerId && a.RequestId == requestId);
        if (answer is not { State: AnswerState.Complete } || answer.ReceivedCharacters >= 16000 || answer.Blocks.Count >= 110) return false;
        answer.State = AnswerState.Receiving; answer.Detail = "Continuing this answer…";
        Changed?.Invoke(answer); return true;
    }
    public StreamAnswer Begin(Guid requestId, Guid answerId, string title, Guid? parent = null)
    {
        if (answers.Count >= Capacity) throw new InvalidOperationException("Answer history is full (100 answers). Close and reopen the answer window to clear it.");
        if (requestId == Guid.Empty || answerId == Guid.Empty || answers.Any(a => a.Id == answerId)) throw new ArgumentException("Answer identifiers must be unique and non-empty.");
        if (parent is not null && !answers.Any(a => a.Id == parent)) throw new ArgumentException("The parent answer is missing.");
        var answer = new StreamAnswer(requestId, answerId, title, parent);
        answers.Add(answer); Changed?.Invoke(answer); return answer;
    }
    public bool Accept(AnswerEvent item)
    {
        var answer = answers.FirstOrDefault(a => a.Id == item.AnswerId && a.RequestId == item.RequestId);
        if (answer is null || answer.State != AnswerState.Receiving) return false;
        if (answer.Seen.TryGetValue(item.Sequence, out var prior))
        {
            if (prior != item) Fail(answer, "Conflicting duplicate event; prior readable text retained.");
            return false;
        }
        if (item.Sequence < answer.NextSequence || item.Sequence > answer.NextSequence + 64 || item.Text.Length > 4096
            || answer.ReceivedCharacters + item.Text.Length > 20000 || answer.Seen.Count >= 4096)
        { Fail(answer, "Response exceeded the demo's ordering or size limit; prior readable text retained."); return false; }
        answer.Seen.Add(item.Sequence, item); answer.Pending.Add(item.Sequence, item);
        answer.ReceivedCharacters += item.Text.Length;
        while (answer.Pending.Remove(answer.NextSequence, out var next))
        {
            answer.NextSequence++;
            answer.Buffer += next.Text.Replace("\r", "");
            int boundary;
            while ((boundary = answer.Buffer.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
            {
                if (!Commit(answer, answer.Buffer[..boundary])) return false;
                answer.Buffer = answer.Buffer[(boundary + 2)..];
            }
            if (answer.Buffer.Length > MaximumParagraphCharacters)
            { Fail(answer, "Paragraph exceeded 1,500 characters; prior readable text retained."); return false; }
            if (next.Kind == AnswerEventKind.Complete)
            {
                if (!Commit(answer, answer.Buffer)) return false;
                answer.Buffer = ""; answer.State = AnswerState.Complete;
            }
            else if (next.Kind == AnswerEventKind.Interrupted)
            { answer.State = AnswerState.Interrupted; answer.Buffer = ""; }
            if (answer.State != AnswerState.Receiving) { answer.Pending.Clear(); break; }
        }
        answer.Detail = answer.State switch
        {
            AnswerState.Complete => "Complete",
            AnswerState.Interrupted => "Interrupted · unfinished paragraph withheld",
            _ when answer.Pending.Count > 0 => "Waiting for missing chunks",
            _ => "Receiving · unfinished paragraphs are buffered"
        };
        Changed?.Invoke(answer); return true;
    }
    public void Supersede(Guid requestId)
    {
        foreach (var answer in answers.Where(a => a.RequestId == requestId && a.State == AnswerState.Receiving))
        { answer.State = AnswerState.Superseded; answer.Detail = "Superseded · displayed paragraphs retained"; answer.Buffer = ""; answer.Pending.Clear(); Changed?.Invoke(answer); }
    }
    public void Fail(Guid answerId, string message)
    {
        var answer = answers.FirstOrDefault(a => a.Id == answerId);
        if (answer is not null && answer.State == AnswerState.Receiving) Fail(answer, message);
    }
    private void Fail(StreamAnswer answer, string message)
    { answer.State = AnswerState.Failed; answer.Detail = message; answer.Buffer = ""; answer.Pending.Clear(); Changed?.Invoke(answer); }
    private bool Commit(StreamAnswer answer, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Length > MaximumParagraphCharacters || answer.Blocks.Count >= MaximumBlocks)
        { Fail(answer, "Response exceeded 128 paragraphs or 1,500 characters per paragraph; prior readable text retained."); return false; }
        answer.Commit(text); return true;
    }
}
