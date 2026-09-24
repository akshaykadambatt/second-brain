using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed class ReaderPlayback
{
    private readonly ReaderSession session;
    private double velocity;
    public bool TimedMode { get; private set; }
    public bool Playing { get; private set; }
    public bool Waiting => Playing && session.AwaitingText && Cursor >= session.Words.Count;
    public double Cursor { get; private set; }
    public double WordsPerMinute { get; private set; } = 150;
    public VoiceFlow Voice { get; }
    public event Action? StateChanged;

    public ReaderPlayback(ReaderSession session)
    {
        this.session = session;
        Voice = new(session);
        session.Changed += change =>
        {
            if (change is ReaderChange.Document or ReaderChange.Position)
            { Voice.Stop(); Cursor = session.Position; Pause(); }
            else if (change == ReaderChange.VoicePosition && !Voice.Active) Cursor = session.Position;
            else if (TimedMode && change is (ReaderChange.Append or ReaderChange.StreamState)) StateChanged?.Invoke();
        };
    }
    public void SetSpeed(double wordsPerMinute)
    {
        if (!double.IsFinite(wordsPerMinute) || wordsPerMinute is < 60 or > 300) throw new ArgumentOutOfRangeException(nameof(wordsPerMinute));
        WordsPerMinute = wordsPerMinute;
    }
    public void Play()
    {
        Voice.Stop();
        if (session.Words.Count == 0 && !session.AwaitingText) return;
        if (Cursor >= session.Words.Count && session.Answer is null) session.Select(0);
        TimedMode = true; Playing = true; velocity = 0; StateChanged?.Invoke();
    }
    public void Pause() { Voice.Stop(); Playing = false; velocity = 0; StateChanged?.Invoke(); }
    public void UseVoice() { Voice.Stop(); Playing = false; TimedMode = false; Cursor = session.Position; velocity = 0; StateChanged?.Invoke(); }
    public void StartVoice() { UseVoice(); Voice.Start(); StateChanged?.Invoke(); }
    public void StopVoice() { Voice.Stop(); StateChanged?.Invoke(); }
    public void Tick(double elapsedSeconds)
    {
        if (Voice.Active) { Voice.Tick(elapsedSeconds); return; }
        if (!Playing || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        var wasWaiting = Waiting;
        var dt = Math.Min(elapsedSeconds, 1d / 30);
        const double smoothing = .3;
        var goal = WordsPerMinute / 60;
        var decay = Math.Exp(-dt / smoothing);
        Cursor = Math.Min(session.Words.Count, Cursor + goal * dt + (velocity - goal) * smoothing * (1 - decay));
        velocity = goal + (velocity - goal) * decay;
        session.SelectTimed((int)Cursor);
        if (Cursor >= session.Words.Count)
        {
            velocity = 0;
            if (!session.AwaitingText) Pause();
            else if (!wasWaiting) StateChanged?.Invoke();
        }
    }
    public void Sentence(int direction)
    {
        var starts = SentenceStarts(session.Words);
        var current = session.Position;
        var next = direction > 0 ? starts.FirstOrDefault(i => i > current, session.Words.Count)
            : starts.LastOrDefault(i => i < current, 0);
        session.Select(next);
    }
    public static IReadOnlyList<int> SentenceStarts(IReadOnlyList<ScriptWord> words)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i + 1 < words.Count; i++)
        {
            var word = words[i];
            var plain = word.Text.TrimEnd('"', '\'', '”', '’', ')', ']');
            var abbreviation = new[] { "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "e.g.", "i.e." }.Contains(plain, StringComparer.OrdinalIgnoreCase)
                || Regex.IsMatch(plain, @"^[A-Z]\.$");
            if (word.Suffix.Contains("\n\n", StringComparison.Ordinal) || !abbreviation && Regex.IsMatch(plain, @"[.!?]$")) starts.Add(i + 1);
        }
        return starts;
    }
}
