using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
            if (!prompt.Extension)
            {
                await delta(prompt.Deeper
                    ? "Choose one representative workflow and agree on the success criteria before starting. Record the baseline so the team can compare the outcome with the current process. Review the findings together and document any limitations before deciding whether to expand the pilot. Keep ownership clear and give everyone a chance to raise concerns while changes are still small and easy to reverse.\n\n"
                    : "Start with a small grounded pilot and measure the results before expanding.\n\n");
                return;
            }
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
        var connection = Guid.NewGuid(); var speechStart = 0d;
        companion.KeepFlowing = false;
        var tapped = main.Panels[0].StreamBlocks.Children.OfType<TextBlock>().SelectMany(b => b.Inlines.OfType<Run>()).ElementAt(10);
        tapped.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        check(main.Session.Position == 10 && main.Playback.Voice.Active, "Reader word tap preserves active voice following");
        SayNextWords();
        check(main.Session.Position == 14, "Shared microphone events move the highlight after a real reader word-tap event");
        main.Panels[0].StreamBlocks.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        check(main.Session.Position == 17 && main.Playback.Voice.Active, "Reader wheel navigation preserves active voice following at its new position");
        SayNextWords();
        check(main.Session.Position == 21, "Shared microphone events move the highlight after a real reader wheel event");
        void SayNextWords()
        {
            var phrase = string.Join(" ", main.Session.Words.Skip(main.Session.Position).Take(4).Select(w => w.Text));
            companion.Observe(new(Guid.NewGuid(), AudioSource.Microphone, connection, new(speechStart++, 1, phrase, true, true, .99f), AudioClock.Now));
        }
        companion.KeepFlowing = false; main.Session.Select(count - 2); companion.Tick();
        check(provider.Extensions.Count == 0, "Turning off continuous flow prevents generation from forward taps");
        main.Session.Select(0); companion.KeepFlowing = true; main.Playback.StartVoice();
        while (main.Session.Position < count - 38)
        {
            var phrase = string.Join(" ", main.Session.Words.Skip(main.Session.Position).Take(Math.Min(6, count - 38 - main.Session.Position)).Select(w => w.Text));
            var before = main.Session.Position;
            companion.Observe(new(Guid.NewGuid(), AudioSource.Microphone, connection, new(speechStart++, 1, phrase, true, true, .99f), AudioClock.Now));
            if (main.Session.Position <= before) throw new InvalidOperationException("Synthetic microphone phrase failed to advance real speech matching.");
            companion.Tick();
        }
        check(provider.Extensions.Count == 1 && run.Active && answers.Requests.Count == 1 && main.Playback.Voice.Active,
            "Synthetic microphone events through real speech matching start same-answer continuation with reading time remaining");
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
        main.Session.Select(run.Fast.WordCount - 1); companion.Tick();
        check(provider.Extensions.Count == 2 && !main.Playback.Voice.Active && !main.Playback.Playing,
            "Forward tapping near the end requests continuation without restarting automatic motion");
        provider.Next.SetResult("END_OF_GROUNDED_ANSWER.\n\n"); await run.Work;
        main.Session.Select(run.Fast.WordCount, fromVoice: true); companion.Tick();
        check(run.FlowFinished && provider.Extensions.Count == 2 && !run.Fast.Blocks.Any(b => b.Text.Contains("END_OF_GROUNDED")), "Exhausted grounded detail ends continuation without showing control text");
        var newer = companion.Ask("What is the next delivery step?")!; await newer.Work;
        companion.Navigate(-1); main.Session.Select(run.Fast.WordCount, fromVoice: true); companion.Tick();
        check(main.Session.Answer == run.Fast && provider.Extensions.Count == 2, "Revisiting an older answer cannot start or select a newer continuation");
        companion.Select(newer.Fast); provider.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        companion.KeepFlowing = false; main.Session.Select(newer.Fast.WordCount - 1); companion.KeepFlowing = true;
        main.Session.Select(newer.Fast.WordCount - 1); companion.Tick();
        main.Session.Select(newer.Fast.WordCount - 2); companion.Tick();
        check(provider.Extensions.Count == 2, "Same-word taps and backward navigation do not request more text");
        companion.KeepFlowing = false; main.Session.Select(newer.Fast.WordCount - 1); companion.KeepFlowing = true;
        main.Playback.Play();
        for (var frame = 0; frame < 90; frame++) main.Playback.Tick(1d / 30);
        check(provider.Extensions.Count == 3 && newer.Active && main.Playback.Playing && main.Playback.Waiting,
            "Timed final-word progress starts continuation before automatic end-of-text pause, without a coordinator tick");
        provider.Next.SetResult("One further grounded point completes the timed continuation.\n\n"); await newer.Work;
        check(main.Playback.Playing && !main.Playback.Waiting && main.Session.Answer == newer.Fast,
            "Timed playback remains ready to read the appended words in the same answer");
        var latest = companion.Ask("How should we verify the final delivery?")!; await latest.Work;
        companion.Navigate(-1); main.Session.Select(newer.Fast.WordCount - 1); companion.Tick();
        check(!newer.FlowFinished && main.Session.Answer == newer.Fast && provider.Extensions.Count == 3,
            "Forward tapping an older, still extensible answer cannot start a continuation or steal selection");
        companion.Select(latest.Fast);
        provider.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Playback.StartVoice();
        main.Session.Select(latest.Fast.WordCount - 1, fromVoice: true); companion.Tick();
        var beforeStop = latest.Fast.WordCount; await companion.Stop();
        check(!latest.Active && latest.Fast.WordCount == beforeStop && main.Session.Answer == latest.Fast, "Stopping cancels pending continuation and preserves the visible answer");
    }
}
