using SecondBrain.Core;

namespace SecondBrain.App;

// UI-thread coordinator. Capture/transcription are owned by MainWindow; this
// consumes their shared transcript stream, never opening another microphone.
internal sealed class CompanionSession : IDisposable
{
    private readonly ReaderSession reader;
    private readonly ReaderPlayback playback;
    private readonly AssistantContext context;
    private readonly AssistantOptions options;
    private readonly IDisposable? provider;
    private readonly Queue<(string Text, double Time)> microphone = new();
    private readonly Queue<(string Question, string Context, Guid Session)> pending = new();
    private readonly HashSet<Guid> readableAnswers = [];
    private Guid microphoneConnection;
    private readonly QuestionUtterance utterance = new();
    private Guid systemConnection;
    private bool disposed;
    public AssistantService Answers { get; }
    public bool Active { get; private set; } = true;
    public StreamAnswer? Selected { get; private set; }
    public string Status { get; private set; } = "Listening for a question from computer audio…";
    public int PendingCount => pending.Count;
    public event Action? Changed;
    public CompanionSession(ReaderSession reader, ReaderPlayback playback, AssistantContext context,
        AssistantService answers, AssistantOptions options, IDisposable? provider = null)
    {
        this.reader = reader; this.playback = playback; this.context = context; Answers = answers; this.options = options; this.provider = provider;
        context.SessionChanged += SessionChanged;
        Answers.Inbox.Changed += Updated; Answers.Changed += AnswerChanged;
        var waiting = new AnswerInbox().Begin(Guid.NewGuid(), Guid.NewGuid(), "Listening for a question…");
        reader.ShowAnswer(waiting);
    }
    private void SessionChanged()
    { pending.Clear(); microphone.Clear(); Answers.CancelAll(); Answers.Questions.Reset(); microphoneConnection = systemConnection = Guid.Empty; utterance.Reset(); }
    public void Observe(LiveSpeech speech)
    {
        if (!Active || AudioClock.Now - speech.ReceivedAt > 1) return;
        if (speech.Source == AudioSource.System)
        {
            if (systemConnection != speech.ConnectionId) { systemConnection = speech.ConnectionId; utterance.Reset(); }
            if (utterance.Observe(speech.Segment, AudioClock.Now) is { } question)
                Received(new(speech.SessionId, "Final", AudioSource.System, speech.Segment.Start, speech.Segment.Start + speech.Segment.Duration, question));
            return;
        }
        if (!string.IsNullOrWhiteSpace(speech.Segment.Text))
        {
            var normalized = QuestionGate.Normalize(speech.Segment.Text);
            if (normalized.Length >= 3) microphone.Enqueue((normalized, AudioClock.Now));
            while (microphone.Count > 20) microphone.Dequeue();
        }
        if (Selected is null || !playback.Voice.Active) return;
        if (microphoneConnection != speech.ConnectionId)
        { if (microphoneConnection != Guid.Empty) playback.Voice.Reanchor(); microphoneConnection = speech.ConnectionId; }
        playback.Voice.Observe(speech.Segment, AudioSource.Microphone);
    }
    private void Received(TranscriptEntry entry)
    {
        if (!Active) return;
        var question = QuestionGate.Detect(entry); if (question is null) return;
        var normalized = QuestionGate.Normalize(question);
        while (microphone.TryPeek(out var old) && AudioClock.Now - old.Time > 5) microphone.Dequeue();
        if (microphone.Any(m => Echo(m.Text, normalized))) return;
        if (pending.Any(p => QuestionGate.Normalize(p.Question) == normalized)) return;
        if (pending.Count >= 3) { Status = "Question queue full. Use the question box for a missed question."; Changed?.Invoke(); return; }
        pending.Enqueue((question, context.Snapshot(), entry.SessionId)); Tick();
    }
    private static bool Echo(string mic, string system)
    {
        var words = system.Split(' ', StringSplitOptions.RemoveEmptyEntries); var micWords = mic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || micWords.Length < 3) return false;
        if ((" " + mic + " ").Contains(" " + system + " ", StringComparison.Ordinal) || (" " + system + " ").Contains(" " + mic + " ", StringComparison.Ordinal)) return true;
        var spoken = micWords.ToHashSet();
        return words.Length >= 4 && words.Count(spoken.Contains) >= Math.Ceiling(words.Length * .85);
    }
    public AnswerRequest? Ask(string question, bool automatic = false, string? snapshot = null, Guid? session = null)
    {
        if (!Active) return null;
        try
        {
            var request = Answers.Ask(question, options, snapshot ?? context.Snapshot(), session ?? context.SessionId, automatic, continuation: true);
            Status = request.Status; Changed?.Invoke(); return request;
        }
        catch (InvalidOperationException ex) { Status = ex.Message; Changed?.Invoke(); return null; }
    }
    public void Tick()
    {
        if (!Active) return;
        if (utterance.Flush(AudioClock.Now) is { } question)
            Received(new(context.SessionId, "Final", AudioSource.System, 0, 0, question));
        if (pending.Count > 0 && Answers.ActiveCount < 3)
        { var next = pending.Dequeue(); Ask(next.Question, true, next.Context, next.Session); }
    }
    private void Updated(StreamAnswer answer)
    {
        if (!Active) return;
        // Switch once when the newest question has a readable opening. Older
        // requests finishing late and continuations must not steal navigation.
        if (answer.WordCount > 0 && readableAnswers.Add(answer.Id)
            && Answers.Requests.LastOrDefault()?.Fast == answer) Select(answer);
        else reader.RefreshAnswer(answer);
        Changed?.Invoke();
    }
    private void AnswerChanged()
    {
        if (Answers.Requests.LastOrDefault() is { } run)
            Status = run.Status + (run.FirstReadableMs is { } ms ? $" · first sentence {ms / 1000:F2}s" : "");
        Changed?.Invoke();
    }
    public void Navigate(int direction)
    {
        var list = Answers.Inbox.Answers.Where(a => a.WordCount > 0).ToList(); if (list.Count == 0) return;
        Select(list[Math.Clamp(list.IndexOf(Selected!) + direction, 0, list.Count - 1)]);
    }
    public void Select(StreamAnswer answer)
    {
        if (Selected is { } prior) prior.SavedPosition = reader.Position;
        Selected = answer; reader.ShowAnswer(answer);
        if (Active) playback.StartVoice();
        Changed?.Invoke();
    }
    public async Task Stop()
    {
        Active = false; pending.Clear(); playback.Pause();
        context.SessionChanged -= SessionChanged;
        await Answers.Stop(); Status = "Answer generation stopped · your readable answer remains available."; Changed?.Invoke();
        Dispose();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        context.SessionChanged -= SessionChanged;
        Answers.Inbox.Changed -= Updated; Answers.Changed -= AnswerChanged; provider?.Dispose();
    }
}
