using System.Globalization;
using SecondBrain.Core;

internal static class DisplayFormatTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Display dates use English month words and clock times use AM or PM independently of machine locale", () =>
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                check(DisplayFormats.Date(new(2026, 9, 27)) == "27-September-2026", "Date order or month language changed");
                check(DisplayFormats.ClockTime(new(2026, 9, 27, 0, 5, 0, TimeSpan.Zero)) == "12:05 AM", "Midnight formatted incorrectly");
                check(DisplayFormats.ClockTime(new(2026, 9, 27, 12, 5, 0, TimeSpan.Zero)) == "12:05 PM", "Noon formatted incorrectly");
                check(DisplayFormats.ClockTime(new(2026, 9, 27, 23, 10, 0, TimeSpan.Zero)) == "11:10 PM", "Evening formatted incorrectly");
                check(DisplayFormats.TryDate("2-January-2026", out var date) && date == new DateOnly(2026, 1, 2), "Display date input failed");
                check(DisplayFormats.TryDate("2026-01-02", out var legacy) && legacy == date && DisplayFormats.TryDate("2 January 2026", out _), "Legacy or spaced input failed");
                check(!DisplayFormats.TryDate("31-February-2026", out _) && DisplayFormats.ParseOptionalDate(" ") is null, "Invalid or blank dates changed meaning");
                var stamp = "2026-09-27T00:05:00+00:00"; var instant = DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture);
                check(DisplayFormats.HistoryLine("abc1234 " + stamp + "Saved notes") == "abc1234 " + stamp + "Saved notes", "Malformed history row was rewritten");
                check(DisplayFormats.HistoryLine("abc1234 " + stamp + " Saved notes") == "abc1234 " + DisplayFormats.LocalDateTime(instant) + " Saved notes", "History lost revision identity or local clock");
                var diff = "AuthorDate: " + stamp + "\n+AuthorDate: " + stamp;
                check(DisplayFormats.HistoryDiff(diff) == "AuthorDate: " + DisplayFormats.LocalDateTime(instant) + "\n+AuthorDate: " + stamp, "Formatting rewrote a quoted source diff line");
            }
            finally { CultureInfo.CurrentCulture = prior; }
        });
        test("Old and word-month meeting headings retain dated retrieval and immutable source text", () =>
        {
            var root = folder("display-date-index"); var path = Path.Combine(root, "updates.md");
            var original = "---\nproject: Cedar\ndate: 2026-01-01\n---\n# Earlier\nInitial note.\n## Meeting update · 2026-09-24\nOld delivery target.\n## Meeting update · 27-September-2026\nNew delivery target.\n";
            File.WriteAllText(path, original); var index = new VaultIndex(); index.Rebuild(root);
            var result = index.Search("delivery", new("Cedar", new DateOnly(2026, 9, 25)), new Dictionary<string, float[]>());
            check(result.Hits.Count == 1 && result.Hits[0].Chunk.Date == new DateOnly(2026, 9, 27) && result.Hits[0].Chunk.Text.Contains("New delivery"), "Displayed heading date broke scoped date filtering");
            check(File.ReadAllText(path) == original, "Display change rewrote existing notes");
        });
    }
}
