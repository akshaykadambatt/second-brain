using System.Globalization;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

// Display and user input only. Persisted dates, source quotes and file identities keep their original representation.
public static class DisplayFormats
{
    public const string DatePattern = "dd-MMMM-yyyy";
    public const string AnswerInstruction = " Format explicit calendar dates as day-full English month-year, for example 27-September-2026. Format times of day using a 12-hour clock with AM or PM, for example 3:10 PM. Preserve the source timezone and do not invent a missing day, month, year or time; relative wording such as Friday can stay relative. Elapsed durations and exact source quotations retain their meaning.";
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;
    public static string Date(DateOnly? date, string missing = "undated") => date?.ToString(DatePattern, English) ?? missing;
    public static string DateText(string text) => TryDate(text, out var date) ? Date(date) : text;
    public static bool TryDate(string? text, out DateOnly date) => DateOnly.TryParseExact(text?.Trim(),
        ["d-MMMM-yyyy", "dd-MMMM-yyyy", "d MMMM yyyy", "dd MMMM yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "dd MMM yyyy", "yyyy-MM-dd"], English, DateTimeStyles.AllowWhiteSpaces, out date);
    public static DateOnly? ParseOptionalDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return TryDate(text, out var date) ? date : throw new FormatException("Use a date such as 27-September-2026, or leave it blank.");
    }
    public static string LocalDateTime(DateTimeOffset value) => value.ToLocalTime().ToString(DatePattern + " · h:mm tt", English);
    public static string ClockTime(DateTimeOffset value) => value.ToString("h:mm tt", English);
    public static string HistoryLine(string line)
    {
        var parts = line.Split(' ', 3);
        return parts.Length == 3 && DateTimeOffset.TryParseExact(parts[1], "yyyy-MM-dd'T'HH:mm:sszzz", English, DateTimeStyles.None, out var date)
            ? parts[0] + " " + LocalDateTime(date) + " " + parts[2] : line;
    }
    public static string HistoryDiff(string diff) => Regex.Replace(diff, @"^(AuthorDate:|CommitDate:)[ \t]+(\S+)[ \t]*\r?$", match =>
        DateTimeOffset.TryParseExact(match.Groups[2].Value, "yyyy-MM-dd'T'HH:mm:sszzz", English, DateTimeStyles.None, out var date)
            ? match.Groups[1].Value + " " + LocalDateTime(date) : match.Value, RegexOptions.Multiline);
}
