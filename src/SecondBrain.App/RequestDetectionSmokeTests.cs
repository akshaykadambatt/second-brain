using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class RequestDetectionSmokeTests
{
    private sealed class Provider : IAnswerProvider
    {
        public readonly List<AssistantPrompt> Calls = [];
        public bool Block;
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { Calls.Add(prompt); await delta("Compare the options using the supplied scope and constraints.\n\n"); if (Block) await Task.Delay(Timeout.Infinite, cancellation); }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var id = Guid.NewGuid(); var connection = Guid.NewGuid(); main.AssistantContext.Observe(new(id, "RunStart", null, 0, 0, ""));
        var provider = new Provider(); var service = new AssistantService(main.Dispatcher, provider, new DiagnosticLog(directory));
        using var companion = new CompanionSession(main.Session, main.Playback, main.AssistantContext, service, new(Deeper: false));
        void Speech(AudioSource source, string text, bool endpoint = true) => companion.Observe(new(id, source, connection, new(0, 1, text, true, endpoint, .99f), AudioClock.Now));
        Speech(AudioSource.Microphone, "Explain the deployment risks"); Speech(AudioSource.System, "Explain the deployment risks");
        check(provider.Calls.Count == 0, "A microphone request echoed through system audio stays suppressed");
        Speech(AudioSource.System, "Please compare", false); check(provider.Calls.Count == 0, "Split request waits for its endpoint");
        Speech(AudioSource.System, "the migration strategies."); await service.Requests.Single().Work;
        check(provider.Calls.Single().Question == "Please compare the migration strategies.", "A punctuation-free directed request reaches the provider through the utterance coordinator");
        Speech(AudioSource.System, "Please compare the migration strategies."); check(service.Requests.Count == 1, "Repeated request is deduplicated");
        Speech(AudioSource.System, "Please do not explain the costs."); check(service.Requests.Count == 1, "Negated request stays quiet");
        provider.Block = true; Speech(AudioSource.System, "Outline the delivery plan."); var pending = service.Requests.Last();
        main.AssistantContext.Observe(new(Guid.NewGuid(), "RunStart", null, 0, 0, "")); await pending.Work;
        check(pending.Cancellation.IsCancellationRequested && !pending.Active, "A new session cancels an in-flight directed request");
        await companion.Stop(); capture(main, System.IO.Path.Combine(directory, "request-detection.png"));
    }
}
