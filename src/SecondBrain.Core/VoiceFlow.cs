namespace SecondBrain.Core;

// Logical matching and continuous motion are separate. This clock does not know
// pixels. Panels smoothly finish accepted progress at their own wrapped line,
// without using predictive pace to move beyond that line.
public sealed class VoiceFlow(ReaderSession session)
{
    private DeepgramProgress progress = new(session);
    private double age = 10, velocity, priorAudioEnd = -1;
    private int priorPosition;
    public bool Active { get; private set; }
    public bool Holding => !Active || age >= .75;
    public double Cursor { get; private set; }
    public double WordsPerMinute { get; private set; } = 150;
    public long EvidenceVersion { get; private set; }
    public long LastEvidenceTimestamp { get; private set; }
    public string Status { get; private set; } = "Voice following paused";
    public double LastMatchMilliseconds { get; private set; }
    public void Start()
    {
        progress = new(session); Cursor = session.Position; priorPosition = session.Position;
        priorAudioEnd = -1; velocity = 0; age = 10; WordsPerMinute = 150; Active = true;
        EvidenceVersion++; Status = "Listening · waiting for a nearby phrase";
    }
    public void Stop() { Active = false; velocity = 0; Status = "Voice following paused"; }
    public void Reanchor() { progress.Reanchor(); velocity = 0; age = 10; }
    public void Reposition()
    {
        progress.Reposition(); Cursor = session.Position; priorPosition = session.Position;
        priorAudioEnd = -1; velocity = 0; age = 10; LastEvidenceTimestamp = 0;
        EvidenceVersion++; Status = "Listening · read from the selected word";
    }
    public bool Observe(SpeechSegment segment, AudioSource source = AudioSource.Microphone)
    {
        if (!Active || source != AudioSource.Microphone) return false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var moved = progress.Observe(segment, source); LastMatchMilliseconds = watch.Elapsed.TotalMilliseconds;
        if (progress.Ignored) return false;
        var match = progress.LastMatch;
        if (!moved)
        {
            if (match is null) { age = Math.Max(age, .5); Status = "Uncertain or off script · slowing to hold"; }
            // A plausible first interim gently influences speed, but cannot
            // move the logical reading position.
            else if (!segment.Final && segment.Duration >= .2)
                WordsPerMinute += .1 * (Math.Clamp(match.Matched / segment.Duration * 60, 60, 300) - WordsPerMinute);
            return false;
        }
        var audioEnd = segment.LastWordEnd ?? segment.Start + segment.Duration;
        var delta = audioEnd - priorAudioEnd;
        if (priorAudioEnd >= 0 && delta is >= .15 and <= 3 && session.Position > priorPosition)
            WordsPerMinute += .3 * (Math.Clamp((session.Position - priorPosition) / delta * 60, 60, 300) - WordsPerMinute);
        else if (segment.Duration is >= .2 and <= 3 && match is not null)
            WordsPerMinute += .2 * (Math.Clamp(match.Matched / segment.Duration * 60, 60, 300) - WordsPerMinute);
        priorAudioEnd = audioEnd; priorPosition = session.Position;
        // Assimilate confirmed word evidence into the estimate. This is not a
        // pixel jump: panels ease toward the accepted word's actual wrapped line.
        // Without this correction, sparse final results accumulate visual lag.
        Cursor = Math.Max(Cursor, Math.Max(0, session.Position - (segment.Final ? .35 : 1.5)));
        age = 0; EvidenceVersion++;
        LastEvidenceTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Status = "Following · " + Math.Round(WordsPerMinute) + " WPM estimated";
        return true;
    }
    public void Tick(double elapsed)
    {
        if (!Active || !double.IsFinite(elapsed) || elapsed <= 0) return;
        age += elapsed; // Silence ages in wall time, including a stalled UI.
        var dt = Math.Min(elapsed, 1d / 30);
        var target = Math.Min(session.Words.Count, session.Position + .65);
        var goal = age < .4 && target > Cursor ? Math.Clamp(WordsPerMinute / 60 + (session.Position - Cursor) * .7, 0, 6) : 0;
        if (Holding) { velocity = 0; Status = "Holding · waiting for clear speech"; return; }
        var decay = Math.Exp(-dt / .16);
        var distance = goal * dt + (velocity - goal) * .16 * (1 - decay);
        velocity = goal + (velocity - goal) * decay;
        Cursor = Math.Max(Cursor, Math.Min(target, Cursor + Math.Max(0, distance)));
    }
}
