using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class MeetingMemorySmokeTests
{
    private sealed class Provider : IAnswerProvider
    {
        public string Context = "";
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { Context = prompt.Conversation; await delta("The earlier decision was to postpone Cobalt until Friday.\n\n"); }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var session = Guid.NewGuid(); main.AssistantContext.Observe(new(session, "Final", AudioSource.System, 1, 2, "We decided to postpone Cobalt until Friday.", Id: "early"));
        for (var i = 0; i < 500; i++) main.AssistantContext.Observe(new(session, "Final", AudioSource.System, i + 3, i + 4, "Ordinary later discussion.", Id: "later" + i));
        var provider = new Provider(); var service = new AssistantService(main.Dispatcher, provider, new DiagnosticLog(directory));
        var request = service.Ask("What was the earlier decision?", new(Deeper: false), main.AssistantContext.Snapshot(), session); await request.Work;
        check(provider.Context.Contains("postpone Cobalt") && provider.Context.Contains("source=early"), "The actual answer provider receives old source-linked context");
        await main.Recorder.StartAsync("test-mic", "test-output"); await Task.Delay(120); await main.Recorder.StopAsync(); var recording = main.Recorder.LastDirectory!; var id = RecordingSession.ReadManifest(recording).Id;
        using (var log = new TranscriptLog(recording, id)) log.Append(new(id, "Final", AudioSource.System, 0, .1, "We agreed to review Cobalt.", Id: "persisted"));
        var original = File.ReadAllBytes(Path.Combine(recording, "transcript.jsonl")); MeetingMemory.Save(recording);
        check(MeetingMemory.Read(recording)?.Excerpts.Single().Id == "persisted" && File.ReadAllBytes(Path.Combine(recording, "transcript.jsonl")).SequenceEqual(original), "Versioned memory sidecar preserves original transcript bytes and provenance");
        File.AppendAllText(Path.Combine(recording, "transcript.jsonl"), "\n");
        try { MeetingMemory.Read(recording); check(false, "Changed provenance rejected"); } catch (InvalidDataException) { check(true, "Changed transcript makes the rebuildable summary stale"); }
        main.MeetingMemoryDetails.IsExpanded = true; await Task.Delay(150); capture(main, Path.Combine(directory, "meeting-memory.png"));
    }
}
