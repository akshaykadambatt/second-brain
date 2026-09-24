using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ContextSmokeTests
{
    private sealed class Provider : IAnswerProvider
    {
        internal readonly List<AssistantPrompt> Prompts = [];
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { Prompts.Add(prompt); await delta(prompt.Deeper ? "Confirm the delivery date with the client before committing.\n\n" : "The planned release is Friday.\n\n"); }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.ContextClient.Text = "Cedar"; main.ContextProject.Text = "Cedar"; main.ContextGoal.Text = "Confirm release scope";
        main.ContextParticipants.Text = "Morgan\nRiley"; main.ContextVocabulary.Text = "handover\nrelease gate";
        var context = main.SaveMeetingContext();
        check(!main.Recorder.HasSession && !main.Voice.Running && context.Client == "Cedar", "Editing and saving a client brief does not start capture");
        check(new SessionContextStore(directory).Load().SelectedProfileId == context.ProfileId, "Client selection and brief survive a fresh settings store");
        File.WriteAllText(Path.Combine(main.Knowledge!.Root, "cedar-release.md"), "---\nproject: Cedar\n---\n# Release\nThe Cedar release is Friday.");
        File.WriteAllText(Path.Combine(main.Knowledge.Root, "acorn-release.md"), "---\nproject: Acorn\n---\n# Release\nACORN-PRIVATE release is Monday.");
        await main.Knowledge.Refresh(true);
        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        var provider = new Provider();
        main.MeetingBriefExpander.IsExpanded = true;
        check(await main.StartCompanion(provider), "The selected client brief starts with the integrated session");
        check(!main.MeetingBriefExpander.IsExpanded, "Starting a meeting returns attention to the answer workspace");
        check(!main.ClientPicker.IsEnabled && !main.ContextEditor.IsEnabled, "Client context is fixed while the session is active");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (main.AssistantContext.SessionId == Guid.Empty) await Task.Delay(20, timeout.Token);
        main.Companion!.Ask("When is the release?");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (main.Companion.Answers.Requests.Count == 0) await Task.Delay(20, timeout.Token);
        await main.Companion.Answers.Requests[^1].Work;
        check(provider.Prompts.Count == 2 && provider.Prompts.All(p => p.Context.Contains("Confirm release scope") && p.Context.Contains("Morgan") && p.Context.Contains("release gate")),
            "Opening and continuation receive the same client goal, participants and vocabulary");
        check(provider.Prompts.All(p => p.Knowledge.Contains("Cedar") && !p.Knowledge.Contains("ACORN-PRIVATE")),
            "A selected project restricts live retrieval to that project");
        var recording = main.Recorder.LastDirectory!; var snapshot = SessionContextStore.ReadSnapshot(recording)!;
        check(snapshot.SessionId == RecordingSession.ReadManifest(recording).Id && snapshot.Context.ProfileId == context.ProfileId, "Recorded context is linked to the correct session and client profile");
        await main.StopCompanion();
        check(main.ClientPicker.IsEnabled && main.ContextEditor.IsEnabled, "Stopping re-enables preparation for the next session");
        main.ContextGoal.Text = "Plan the next phase"; main.SaveMeetingContext();
        check(SessionContextStore.ReadSnapshot(recording)!.Context.Goal == "Confirm release scope", "Later profile edits preserve the original meeting brief");
        main.MeetingBriefExpander.IsExpanded = true; main.LiveTab.IsSelected = true;
        capture(main, Path.Combine(directory, "client-brief.png"));
    }
}
