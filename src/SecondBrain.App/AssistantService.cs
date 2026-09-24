using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class AnswerRequest(Guid id, string question, Guid sessionId, StreamAnswer fast, StreamAnswer? deeper)
{
    public Guid Id { get; } = id;
    public Guid SessionId { get; } = sessionId;
    public string Question { get; } = question;
    public StreamAnswer Fast { get; } = fast;
    public StreamAnswer? Deeper { get; } = deeper;
    public CancellationTokenSource Cancellation { get; } = new();
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    public Task Work { get; set; } = Task.CompletedTask;
    public bool Active { get; set; } = true;
    public string Status { get; set; } = "Generating quick answer…";
    public double? FirstTextMs { get; set; }
    public double? FirstReadableMs { get; set; }
    public double? CompletedMs { get; set; }
}

internal sealed class AssistantService(Dispatcher dispatcher, IAnswerProvider provider, DiagnosticLog log)
{
    public AnswerInbox Inbox { get; } = new();
    public QuestionGate Questions { get; } = new();
    public List<AnswerRequest> Requests { get; } = [];
    public event Action? Changed;
    public int ActiveCount => Requests.Count(r => r.Active);
    public AnswerRequest Ask(string question, AssistantOptions options, string conversation, Guid sessionId, bool automatic = false)
    {
        dispatcher.VerifyAccess(); question = question.Trim();
        if (!options.IsValid) throw new InvalidOperationException("Check the model names, reasoning settings and context length.");
        if (question.Length is < 3 or > 2000) throw new InvalidOperationException("Enter a question between 3 and 2,000 characters.");
        if (ActiveCount >= 3) throw new InvalidOperationException("Three questions are already generating. Cancel or wait before asking another.");
        if (Inbox.Answers.Count > AnswerInbox.Capacity - (options.Deeper ? 2 : 1)) throw new InvalidOperationException("Answer history is full. Close and reopen AI answers to clear it.");
        if (!Questions.Accept(question, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, automatic)) throw new InvalidOperationException("Duplicate or recently answered question suppressed. Wait two minutes to ask it again.");
        var id = Guid.NewGuid(); var title = question.Length > 95 ? question[..95] + "…" : question;
        var fast = Inbox.Begin(id, Guid.NewGuid(), "Quick · " + title);
        var deeper = options.Deeper ? Inbox.Begin(id, Guid.NewGuid(), "Deeper · " + title, fast.Id) : null;
        var run = new AnswerRequest(id, question, sessionId, fast, deeper); Requests.Add(run);
        run.Work = Run(run, options, conversation); Changed?.Invoke(); return run;
    }
    private async Task Run(AnswerRequest run, AssistantOptions options, string conversation)
    {
        try
        {
            await Generate(run.Fast, options.FastModel, options.FastEffort, false);
            if (run.Deeper is { } deep && run.Active)
            { run.Status = "Quick answer ready · generating deeper answer…"; Changed?.Invoke(); await Generate(deep, options.DeepModel, options.DeepEffort, true); }
            if (run.Active) { run.Status = "Complete · choose an answer to read"; run.CompletedMs = run.Clock.Elapsed.TotalMilliseconds; }
        }
        catch (Exception ex)
        {
            if (run.Active)
            {
                run.Status = ex is OperationCanceledException ? "AI request timed out. Earlier paragraphs retained." : ex is InvalidOperationException or InvalidDataException or IOException ? ex.Message : "AI connection failed. Check the network and try again.";
                Inbox.Fail(run.Fast.Id, run.Status); if (run.Deeper is { } deep) Inbox.Fail(deep.Id, run.Status);
                log.Write("AI request=" + run.Id + "; failure=" + ex.GetType().Name);
                Questions.Forget(run.Question);
            }
        }
        finally
        {
            run.Active = false; run.CompletedMs ??= run.Clock.Elapsed.TotalMilliseconds;
            log.Write($"AI timing request={run.Id}; firstTextMs={run.FirstTextMs:F0}; firstReadableMs={run.FirstReadableMs:F0}; completedMs={run.CompletedMs:F0}; fastState={run.Fast.State}; deeperState={run.Deeper?.State}");
            run.Cancellation.Dispose(); Changed?.Invoke();
        }
        async Task Generate(StreamAnswer answer, string model, string effort, bool deeper)
        {
            var sequence = 0; var buffer = new ReadableAnswerBuffer();
            var prompt = new AssistantPrompt(run.Id, run.Question, options.Context, conversation, model, effort, deeper);
            await provider.Generate(prompt, async text => await dispatcher.InvokeAsync(() =>
            {
                if (!run.Active || run.Cancellation.IsCancellationRequested) return;
                run.FirstTextMs ??= run.Clock.Elapsed.TotalMilliseconds;
                foreach (var block in buffer.Push(text)) Deliver(block);
            }), run.Cancellation.Token);
            if (!run.Active) return;
            foreach (var block in buffer.Push("", true)) Deliver(block);
            if (answer.WordCount == 0) throw new InvalidOperationException("Provider returned no readable answer.");
            Inbox.Accept(new(run.Id, answer.Id, sequence++, Kind: AnswerEventKind.Complete));
            void Deliver(string block)
            {
                run.FirstReadableMs ??= run.Clock.Elapsed.TotalMilliseconds;
                if (!Inbox.Accept(new(run.Id, answer.Id, sequence++, block + "\n\n"))) throw new InvalidDataException("Reader rejected the answer stream.");
                Questions.RememberAnswer(block); Changed?.Invoke();
            }
        }
    }
    public void CancelAll()
    {
        dispatcher.VerifyAccess();
        foreach (var run in Requests.Where(r => r.Active))
        { run.Active = false; run.Status = "Canceled · readable paragraphs retained"; run.Cancellation.Cancel(); Inbox.Supersede(run.Id); Questions.Forget(run.Question); }
        Changed?.Invoke();
    }
    public Task Stop() { CancelAll(); return Task.WhenAll(Requests.Select(r => r.Work)); }
}
