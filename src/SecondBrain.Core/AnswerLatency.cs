using System.Text.Json;

namespace SecondBrain.Core;

public sealed record QuestionTiming(double DetectedAt, double? TranscriptReceivedAt = null, double? SpeechEndedAt = null);
public sealed record AnswerLatency(Guid RequestId, Guid SessionId, bool Automatic, bool FirstInSession,
    double? SpeechToReadableMs, double? TranscriptionMs, double? DetectionMs, double? QueueMs,
    double? RetrievalMs, double? GenerationToReadableMs, double? RequestToReadableMs, double? CompletionMs)
{
    public static AnswerLatency Measure(Guid request, Guid session, bool automatic, bool first, double createdAt,
        QuestionTiming? trigger, double? retrievalMs, double? generationStartedMs, double? readableMs, double? completionMs)
    {
        var readyAt = readableMs is { } ms && double.IsFinite(ms) && ms >= 0 ? createdAt + ms / 1000 : (double?)null;
        return new(request, session, automatic, first,
            automatic ? Difference(readyAt, trigger?.SpeechEndedAt) : null,
            Difference(trigger?.TranscriptReceivedAt, trigger?.SpeechEndedAt), Difference(trigger?.DetectedAt, trigger?.TranscriptReceivedAt),
            Difference(createdAt, trigger?.DetectedAt), Valid(retrievalMs), Difference(readableMs, generationStartedMs, 1), Valid(readableMs), Valid(completionMs));
    }
    private static double? Valid(double? value) => value is { } v && double.IsFinite(v) && v >= 0 ? v : null;
    private static double? Difference(double? end, double? start, double scale = 1000)
        => end is { } e && start is { } s && double.IsFinite(e) && double.IsFinite(s) && e >= s ? (e - s) * scale : null;
}
public sealed record LatencySummary(int Requests, int Readable, int SpeechTimed, double? SpeechP50, double? SpeechP95, double? RequestP50, double? RequestP95);
public sealed record LatencyReport(int SchemaVersion, string Definition, AnswerLatency[] Samples)
{
    public static LatencyReport Create(IEnumerable<AnswerLatency> samples) => new(1,
        "FirstInSession separates the first request (cold-session proxy) from subsequent requests; provider cache state is not known. Speech end is the provider's last timed word mapped to the session audio clock. Readable means a complete text block is ready, not a physical display measurement. Missing/invalid timestamps stay null.", samples.ToArray());
    public static LatencySummary Summarize(IEnumerable<AnswerLatency> source)
    {
        var samples = source.ToArray(); var speech = samples.Select(s => s.SpeechToReadableMs).OfType<double>().Where(v => double.IsFinite(v) && v >= 0).Order().ToArray();
        var request = samples.Select(s => s.RequestToReadableMs).OfType<double>().Where(v => double.IsFinite(v) && v >= 0).Order().ToArray();
        static double? Percentile(double[] values, double percentile) => values.Length == 0 ? null : values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
        return new(samples.Length, request.Length, speech.Length, Percentile(speech, .5), Percentile(speech, .95), Percentile(request, .5), Percentile(request, .95));
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, this, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
