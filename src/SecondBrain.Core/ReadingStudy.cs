namespace SecondBrain.Core;

public sealed record ReadingTrial(int Session, int Number, string Kind, int Width, int Font, string Text, string Question, string[] Answers, int CorrectAnswer);
public sealed record ReadingResult(DateTimeOffset At, int Session, int Trial, string Kind, int Width, int Font, double Wpm,
    int Mistakes, int Corrections, int LostPlace, int Comfort, int EyeStrain, bool ComprehensionCorrect, double Seconds);

public static class ReadingStudy
{
    private static readonly string[] Topics = ["search service", "reporting service", "payment service", "notification service", "storage service"];
    public static ReadingTrial Trial(int session, int number, int preferredWidth)
    {
        if (session is < 1 or > 2 || number is < 1 or > 5 || preferredWidth is not (28 or 36 or 48)) throw new ArgumentOutOfRangeException(nameof(number));
        var widths = session == 1 ? new[] { 36, 28, 48 } : new[] { 48, 36, 28 };
        var font = number <= 3 ? 32 : session == 1 ? (number == 4 ? 32 : 38) : (number == 4 ? 38 : 32);
        var width = number <= 3 ? widths[number - 1] : preferredWidth;
        var topic = Topics[(number + session - 2) % Topics.Length];
        var day = session == 1 ? "Tuesday" : "Thursday";
        var minutes = 10 + number * 5;
        var text = $"Today we are reviewing the {topic}. The team will test the update on {day}, starting at nine thirty. "
            + $"We will observe the results for {minutes} minutes before making a decision. The API must return a clear error when a request fails. "
            + "First, check the logs and compare the timestamps. Then confirm that a retry does not create a duplicate record. "
            + "If the checks pass, we can continue with the next group of users. If they fail, pause the release and keep the previous version available. "
            + "Please write down the decision and its reason so that another person can understand it later.";
        var answers = new[] { $"{minutes} minutes", $"{minutes + 10} minutes", $"{minutes - 5} minutes" };
        var rotation = (session + number) % 3;
        return new(session, number, number <= 3 ? "Width" : "Font", width, font, text,
            "How long should the team observe the results?", answers.Skip(rotation).Concat(answers.Take(rotation)).ToArray(), (3 - rotation) % 3);
    }
    public static int RecommendedWidth(IEnumerable<ReadingResult> results) => results.Where(r => r.Kind == "Width")
        .GroupBy(r => r.Width).OrderBy(g => g.Average(r => r.Mistakes + r.Corrections))
        .ThenByDescending(g => g.Average(r => r.Comfort)).Select(g => g.Key).FirstOrDefault(36);
    public static bool Complete(IEnumerable<ReadingResult> results)
    {
        var keys = results.Select(r => (r.Session, r.Trial)).ToHashSet();
        return Enumerable.Range(1, 2).All(s => Enumerable.Range(1, 5).All(t => keys.Contains((s, t))));
    }
    public static int RecommendedFont(IEnumerable<ReadingResult> results) => results.Where(r => r.Kind == "Font")
        .GroupBy(r => r.Font).OrderBy(g => g.Average(r => r.Mistakes + r.Corrections))
        .ThenByDescending(g => g.Average(r => r.Comfort)).Select(g => g.Key).FirstOrDefault(32);
}
