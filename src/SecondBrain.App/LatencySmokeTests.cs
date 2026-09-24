using System.IO;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class LatencySmokeTests
{
    private sealed class Lookup : IKnowledgeSearch
    {
        public async Task<KnowledgeResult> Search(string question, CancellationToken cancellation)
        { await Task.Delay(40, cancellation); return new([], "Synthetic empty evidence"); }
    }
    private sealed class Provider : IAnswerProvider
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { await Release.Task.WaitAsync(cancellation); await Task.Delay(40, cancellation); await delta("A complete synthetic opening is ready.\n\n"); }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check)
    {
        var provider = new Provider(); var service = new AssistantService(main.Dispatcher, provider, new(directory), new Lookup());
        var context = new AssistantContext(); var id = Guid.NewGuid(); context.Observe(new(id, "RunStart", null, 0, 0, ""));
        var origin = AudioClock.Now - 10;
        using var companion = new CompanionSession(main.Session, main.Playback, context, service, new(Deeper: false), sessionClockOrigin: () => origin);
        var connection = Guid.NewGuid();
        void Observe(string question, bool final = true, double? end = 9.5) => companion.Observe(new(id, AudioSource.System, connection,
            new(9, 1, question, true, final, .99f), AudioClock.Now) { SpeechEndedSeconds = end });
        Observe("What is the first milestone?"); Observe("When is the second milestone?"); Observe("Who owns the third milestone?");
        Observe("How do we validate the fourth milestone?");
        check(service.ActiveCount == 3 && companion.PendingCount == 1, "Timing instrumentation preserves the three-request limit and question queue");
        await Task.Delay(100); provider.Release.SetResult(); await Task.WhenAll(service.Requests.Select(r => r.Work));
        companion.Tick(); await service.Requests[^1].Work;
        var queued = service.Requests[^1].Latency;
        check(queued.QueueMs >= 100 && queued.RequestId == service.Requests[^1].Id && queued.SessionId == id, "Queued question retains its original detection clock and request/session identity");
        check(service.Requests.All(r => r.Latency.TranscriptionMs >= 400 && r.Latency.RetrievalMs >= 30 && r.Latency.GenerationToReadableMs >= 30), "Real asynchronous pipeline separates mapped speech end, retrieval and generation");
        check(service.Requests.Count(r => r.FirstInSession) == 1 && service.Requests.All(r => r.Latency.SpeechToReadableMs >= r.Latency.RequestToReadableMs), "First and subsequent requests retain complete speech-to-readable timing");
        Observe("Where should we review the fifth milestone?", final: false);
        await Task.Delay(1100); companion.Tick(); await service.Requests[^1].Work;
        check(service.Requests[^1].Latency.DetectionMs >= 1000, "Quiet endpoint fallback includes detection waiting without resetting speech end");
        connection = Guid.NewGuid(); Observe("Why is the sixth milestone important?", end: null); await service.Requests[^1].Work;
        check(service.Requests[^1].Latency.SpeechToReadableMs is null, "Reconnect with absent word timing stays unknown");
        var typed = companion.Ask("Describe the seventh milestone")!; await typed.Work;
        check(!typed.Latency.Automatic && typed.Latency.SpeechToReadableMs is null && typed.Latency.RequestToReadableMs > 0, "Typed requests cannot claim a speech latency measurement");
        var canceled = companion.Ask("Describe the eighth milestone")!; await companion.Stop();
        check(canceled.Latency.RequestToReadableMs is null && canceled.Latency.CompletionMs is not null, "Cancellation remains a counted request without an invented readable result");
        var report = LatencyReport.Create(service.Requests.Select(r => r.Latency)); report.Save(Path.Combine(directory, "synthetic-latency.json"));
        var read = JsonSerializer.Deserialize<LatencyReport>(File.ReadAllText(Path.Combine(directory, "synthetic-latency.json")))!;
        check(read.Samples.Length == 8 && LatencyReport.Summarize(read.Samples).Readable == 7, "Export round-trips missing and successful timing samples");
    }
}

