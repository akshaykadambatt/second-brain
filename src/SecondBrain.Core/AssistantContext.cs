using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record AssistantOptions(string FastModel = "gpt-6-luna", string DeepModel = "gpt-6-sol",
    string FastEffort = "none", string DeepEffort = "medium", bool Deeper = true, string Context = "", bool AllowGeneralGuidance = false)
{
    public bool IsValid => ValidModel(FastModel) && ValidModel(DeepModel) && Context is not null && Context.Length <= 16000
        && new[] { "none", "low", "medium", "high" }.Contains(FastEffort) && new[] { "none", "low", "medium", "high" }.Contains(DeepEffort);
    private static bool ValidModel(string text) => text is not null && Regex.IsMatch(text, @"^[a-zA-Z0-9][a-zA-Z0-9._:-]{1,100}$");
    public bool QuietSuggestions { get; init; }
}

// UI-thread owned, bounded current-session context. Source transcript files remain
// authoritative; this is only the recent conversation window for live answers.
public sealed class AssistantContext
{
    private readonly Queue<TranscriptEntry> entries = new();
    private readonly HashSet<string> seen = [];
    private readonly Queue<string> seenOrder = new();
    public MeetingMemory Memory { get; } = new();
    private int characters;
    public Guid SessionId { get; private set; }
    public bool Truncated { get; private set; }
    public event Action<TranscriptEntry>? Received;
    public event Action? SessionChanged;
    public void MarkGap() => Truncated = true;
    public void Observe(TranscriptEntry entry)
    {
        if (entry.SessionId != SessionId)
        { SessionId = entry.SessionId; entries.Clear(); seen.Clear(); seenOrder.Clear(); Memory.Clear(); characters = 0; Truncated = false; SessionChanged?.Invoke(); }
        if (entry.Kind is not ("Final" or "Gap")) return;
        if (entry.Id is not null && !seen.Add(entry.Id)) return;
        if (entry.Id is not null) { seenOrder.Enqueue(entry.Id); if (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue()); }
        var bounded = entry with { Text = entry.Text.Length > 4000 ? entry.Text[..4000] : entry.Text };
        entries.Enqueue(bounded); characters += bounded.Text.Length + 64;
        while (entries.Count > 100 || characters > 16000)
        { var removed = entries.Dequeue(); characters -= removed.Text.Length + 64; Memory.Observe(removed); Truncated = true; }
        if (entry.Text.Length > 4000) Truncated = true;
        Received?.Invoke(bounded);
    }
    public string Snapshot() => Memory.Snapshot(3500) + (Truncated ? "[Some earlier context is omitted.]\n" : "") + string.Join("\n", entries.Select(e =>
        $"[{e.Start:F2}s {e.Source?.ToString() ?? "Session"} {e.Kind}] {e.Text}"));
}

public sealed class QuestionGate
{
    private readonly Dictionary<string, double> recent = [];
    private readonly Queue<string> generated = new();
    private double lastAutomatic = -100;
    public static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
    public void RememberAnswer(string text)
    { generated.Enqueue(Normalize(text)); while (generated.Count > 20) generated.Dequeue(); }
    public bool Accept(string question, double now, bool automatic, double automaticInterval = 10)
    {
        var normalized = Normalize(question);
        if (normalized.Length < 3 || question.Length > 2000) return false;
        foreach (var key in recent.Where(p => now - p.Value > 120).Select(p => p.Key).ToArray()) recent.Remove(key);
        if (recent.ContainsKey(normalized) || automatic && now - lastAutomatic < automaticInterval) return false;
        if (automatic && generated.Any(a => a.Contains(normalized, StringComparison.Ordinal))) return false;
        if (recent.Count >= 128) recent.Remove(recent.MinBy(p => p.Value).Key);
        recent[normalized] = now; if (automatic) lastAutomatic = now; return true;
    }
    public void Reset() { recent.Clear(); generated.Clear(); lastAutomatic = -100; }
    public void Forget(string question) => recent.Remove(Normalize(question));
    public static string? Detect(TranscriptEntry entry)
    {
        if (entry.Kind != "Final" || entry.Source != AudioSource.System || entry.Text.Length > 2000) return null;
        var text = entry.Text.Trim();
        // English, directed requests only. Reported speech and ordinary plans are not requests.
        var sentence = Regex.Split(text, @"(?<=[.!?])\s+").LastOrDefault(s => s.EndsWith('?'));
        if (sentence is not null && Normalize(sentence).Split(' ').Length >= 3) return sentence;
        if (Regex.IsMatch(text, @"^(what|why|how|when|where|who|which|can you|could you|would you|do you|did you|are you|is there)\b", RegexOptions.IgnoreCase)
            && Normalize(text).Split(' ').Length >= 4) return text;
        foreach (var part in Regex.Split(text, @"(?<=[.!?])\s+").Reverse())
        {
            var normalized = Normalize(part);
            if (normalized.Split(' ').Length < 3 || Regex.IsMatch(normalized, @"^(please )?(do not|don t|dont|never|no need|you don t need)\b")) continue;
            if (Regex.IsMatch(normalized, @"^(please )?(explain|clarify|summarize|summarise|compare|describe|recommend|outline|walk (me|us) through|tell (me|us)|help (me|us)|give (me|us)|share your)\b")
                || Regex.IsMatch(normalized, @"^(i d|we d|i would|we would) like (you to|your (view|recommendation|opinion|assessment)|an explanation|a comparison)\b")
                || Regex.IsMatch(normalized, @"^(i|we) need (you to (explain|clarify|compare|describe|outline)|your (view|recommendation|assessment))\b")) return part.Trim();
        }
        return null;
    }
}

public sealed record AssistantPrompt(Guid RequestId, string Question, string Context, string Conversation, string Model, string Effort, bool Deeper,
    bool Continuation = false, string Opening = "", string Knowledge = "")
{
    public bool Extension { get; init; }
    public AnswerRefinement? Refinement { get; init; }
    public string OriginalAnswer { get; init; } = "";
    public bool AllowGeneralGuidance { get; init; }
}
public interface IAnswerProvider
{
    Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation);
}

public enum AnswerRefinement { Shorter, Explain, Example }
