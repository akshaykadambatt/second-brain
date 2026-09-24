using SecondBrain.Core;

internal static class LatencyTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Latency separates speech, transcription, detection, queue and generation clocks", () =>
        {
            var sample = AnswerLatency.Measure(Guid.NewGuid(), Guid.NewGuid(), true, true, 10,
                new(9.9, 9.8, 9.6), 30, 30, 100, 200);
            bool Near(double? value, double expected) => value is { } v && Math.Abs(v - expected) < .001;
            check(Near(sample.SpeechToReadableMs, 500) && Near(sample.TranscriptionMs, 200) && Near(sample.DetectionMs, 100)
                && Near(sample.QueueMs, 100) && Near(sample.RetrievalMs, 30) && Near(sample.GenerationToReadableMs, 70), "Pipeline stages were conflated");
            var unknown = AnswerLatency.Measure(Guid.NewGuid(), Guid.NewGuid(), true, false, 10, new(9.9), null, 0, 100, 200);
            check(unknown.SpeechToReadableMs is null && unknown.TranscriptionMs is null && unknown.RequestToReadableMs == 100, "Unknown speech end was invented");
            var invalid = AnswerLatency.Measure(Guid.NewGuid(), Guid.NewGuid(), false, false, 10, new(11, double.NaN, 12), -1, 200, 100, 200);
            check(invalid.SpeechToReadableMs is null && invalid.QueueMs is null && invalid.RetrievalMs is null && invalid.GenerationToReadableMs is null, "Invalid/typed timing should not claim speech latency");
        });
        test("Latency summaries count missing results and keep cold and warm groups separate", () =>
        {
            var values = new double?[] { 100, 200, 400, null };
            var samples = values.Select((value, index) => AnswerLatency.Measure(Guid.NewGuid(), Guid.NewGuid(), true, index == 0, 10, new(10, 10, 10), 0, 0, value, value)).ToArray();
            var summary = LatencyReport.Summarize(samples);
            check(summary.Requests == 4 && summary.Readable == 3 && summary.SpeechTimed == 3 && summary.SpeechP50 is > 199.9 and < 200.1 && summary.SpeechP95 is > 399.9 and < 400.1,
                "Summary hid failures or used the wrong percentile");
            check(LatencyReport.Summarize(samples.Where(s => s.FirstInSession)).Requests == 1 && LatencyReport.Summarize(samples.Where(s => !s.FirstInSession)).Requests == 3,
                "First and subsequent requests were mixed");
            check(LatencyReport.Summarize([]).SpeechP95 is null, "Empty statistics should remain unavailable");
        });
    }
}

