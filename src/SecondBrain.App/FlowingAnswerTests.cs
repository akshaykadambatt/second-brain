using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class FlowingAnswerTests
{
    private sealed class Provider : IAnswerProvider
    {
        internal readonly List<AssistantPrompt> Extensions = [];
        internal TaskCompletionSource<string> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        {
            if (!prompt.Extension) { await delta(string.Join(" ", Enumerable.Repeat("grounded", prompt.Deeper ? 60 : 12)) + ".\n\n"); return; }
            Extensions.Add(prompt); var text = await Next.Task.WaitAsync(cancellation); await delta(text);
        }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check)
    {
        while (main.Panels.Count < 4) main.AddReader();
        var provider = new Provider(); var answers = new AssistantService(main.Dispatcher, provider, new(directory));
        using var companion = new CompanionSession(main.Session, main.Playback, new(), answers, new());
        var run = companion.Ask("How can we explain the delivery approach?")!; await run.Work;
        var original = run.Fast.Blocks[0]; var count = run.Fast.WordCount;
        main.Session.Select(count - 2); companion.Tick();
        check(provider.Extensions.Count == 0, "Manual navigation near the end does not trigger generation");
        main.Playback.StartVoice(); main.Session.Select(count - 1, fromVoice: true); companion.Tick();
        check(provider.Extensions.Count == 1 && run.Active && answers.Requests.Count == 1, "Near-end reading requests continuation inside the same request");
        companion.Tick(); check(provider.Extensions.Count == 1, "Repeated ticks cannot duplicate an in-flight continuation");
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        main.Playback.Pause(); main.Session.Select(3);
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var positions = main.Panels.Select(p => p.WordScreenY(3)).ToArray();
        provider.Next.SetResult(string.Join(" ", Enumerable.Repeat("detail", 60)) + ".\n\n"); await run.Work;
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(run.Fast.WordCount > count && ReferenceEquals(original, run.Fast.Blocks[0]) && main.Session.Answer == run.Fast && main.Session.Position == 3 && answers.Inbox.Answers.Count == 1,
            "Appended detail preserves the active answer, earlier word identity and manual reading position");
        check(main.Panels.Select((p, i) => Math.Abs(p.WordScreenY(3) - positions[i]) <= 1).All(v => v), "Appends preserve text position on four independently laid-out readers");
        check(provider.Extensions[0].Opening.Contains("grounded") && provider.Extensions[0].RequestId == run.Id, "Continuation receives the existing full answer and request identity");
        provider.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Playback.StartVoice(); main.Session.Select(run.Fast.WordCount - 1, fromVoice: true); companion.Tick();
        provider.Next.SetResult("END_OF_GROUNDED_ANSWER.\n\n"); await run.Work;
        main.Session.Select(run.Fast.WordCount, fromVoice: true); companion.Tick();
        check(run.FlowFinished && provider.Extensions.Count == 2 && !run.Fast.Blocks.Any(b => b.Text.Contains("END_OF_GROUNDED")), "Exhausted grounded detail ends continuation without showing control text");
        var newer = companion.Ask("What is the next delivery step?")!; await newer.Work;
        companion.Navigate(-1); main.Session.Select(run.Fast.WordCount, fromVoice: true); companion.Tick();
        check(main.Session.Answer == run.Fast && provider.Extensions.Count == 2, "Revisiting an older answer cannot start or select a newer continuation");
        companion.Select(newer.Fast); provider.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Session.Select(newer.Fast.WordCount - 1, fromVoice: true); companion.Tick();
        var beforeStop = newer.Fast.WordCount; await companion.Stop();
        check(!newer.Active && newer.Fast.WordCount == beforeStop && main.Session.Answer == newer.Fast, "Stopping cancels pending continuation and preserves the visible answer");
    }
}
