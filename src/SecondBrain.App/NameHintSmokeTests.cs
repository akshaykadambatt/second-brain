using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class NameHintSmokeTests
{
    private sealed class FakeCapture : IMeetingCapture { public void Dispose() { } }
    private sealed class Provider : IAnswerProvider
    { public Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation) => delta("Synthetic grounded response.\n\n"); }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, bool meet = false)
    {
        var adapter = meet ? MeetSpeakingLabels.Adapter : "chrome-teams-speaking-en-v1";
        var fixture = new Window { Title = "Synthetic name-hint fixture", Width = 400, Height = 220, ShowActivated = false, Background = Brushes.Chartreuse };
        using var visuals = new MeetingVisuals(main.Dispatcher);
        Action<MeetingFrame>? publish = null; visuals.Factory = (_, callback, _) => { publish = callback; return new FakeCapture(); };
        var status = ""; var names = new TeamsHintSession(visuals, text => status = text);
        using var release = new ManualResetEventSlim();
        try
        {
            fixture.Show(); await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var target = MeetingWindow.List(true).Single(w => w.Handle == new WindowInteropHelper(fixture).Handle);
            check(!TeamsWindowHints.Read(target, ["Morgan"]).Supported, "Non-Chrome selected windows remain audio-only");
            var observed = await Task.Run(() => TeamsWindowHints.Read(target with { ProcessName = "chrome" }, ["Morgan"])).WaitAsync(TimeSpan.FromSeconds(3));
            check(!observed.Supported, "Native accessibility inspection rejects a synthetic window without a supported document URL");
            visuals.Select(target); visuals.Enable(true); visuals.Listen(true);
            var session = Guid.NewGuid(); var origin = AudioClock.Now; names.Start(session, origin, ["Morgan"]);
            names.Read = (_, _) => new(true, ["Morgan"], "Synthetic single speaker", adapter);
            for (var i = 0; i < 6; i++)
            {
                publish!(new(DateTimeOffset.UtcNow, 1, 1, [100, 100, 100, 255]));
                await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                names.Tick(); await Task.Delay(520);
            }
            var detail = new TranscriptDetail(1, session, "fixture-turn", AudioSource.System, Guid.NewGuid(), .5, 2, "Synthetic speech",
                [new("fixture-word", "speech", .5, 2, .9f, 0, null, "speaker1", "Speaker 1")]);
            check(names.Resolve(detail) is { Name: "Morgan" } resolved && resolved.Adapter == adapter, "Fresh stable window observations correlate with a single timed audio speaker");
            visuals.Clear(); names.Tick(); check(names.Resolve(detail) is null, "Clearing the chosen window invalidates prior observations");
            visuals.Select(target); visuals.Enable(true); names.Start(session, AudioClock.Now, ["Morgan"]);
            publish!(new(DateTimeOffset.UtcNow, 1, 1, [100, 100, 100, 255]));
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            names.Read = (_, _) => { release.Wait(); return new(true, ["Late name"], "Late result"); };
            names.Tick(); await Task.Delay(900);
            check(status.Contains("timed out"), "A stalled accessibility provider times out without blocking the UI");
            names.End(); visuals.Clear(); release.Set(); await Task.Delay(100);
            check(names.Resolve(detail) is null && status != "Late result", "Late window inspection cannot repopulate a stopped session");
        }
        finally { release.Set(); names.End(); fixture.Close(); }

        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-offline-key");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        main.LocalParticipantName.Text = "Local fixture";
        check(await main.StartCompanion(new Provider()), "Integrated audio starts without enabling screen capture");
        main.Transcriber.ResolveNameHint = detail => detail.Source == AudioSource.System && detail.Words.Length > 0 && detail.Words.All(w => w.SpeakerId is not null)
            && detail.Words.Select(w => w.SpeakerId).Distinct().Count() == 1
            ? new(1, detail.SessionId, detail.SegmentId, SpeakerNameHints.Binding(detail), "Remote fixture", detail.Start, detail.End, adapter) : null;
        await Task.Delay(2300); await main.StopCompanion();
        var path = main.Recorder.LastDirectory!; var records = TranscriptDetails.Read(path);
        var hints = SpeakerNameHints.Read(path, records, out var warning);
        check(File.ReadAllText(Path.Combine(path, "speaker-name-hints.jsonl")).Contains(adapter), "The selected platform adapter is preserved in persisted provenance");
        check(hints.Count > 0 && warning.Length == 0 && records.Where(r => r.Source == AudioSource.System).SelectMany(r => r.Words).All(w => w.SpeakerLabel != "Remote fixture"),
            "Production transcript writer stores hints separately without rewriting source speaker labels");
        var viewer = await main.OpenTranscript(path);
        check(viewer is not null && viewer.Review.Words.Any(w => w.Speaker == "Remote fixture (screen hint)"), "Meeting review labels optional name hints explicitly");
        var word = viewer!.Review.Words.First(w => w.Speaker == "Remote fixture (screen hint)"); viewer.Review.Assign([word.Id], "Corrected fixture"); viewer.Close();
        viewer = await main.OpenTranscript(path); check(viewer!.Review.Words.Single(w => w.Id == word.Id).Speaker == "Corrected fixture", "Manual corrections survive reopening and take priority over hints"); viewer.Close();
        check(!main.Visuals.Running && main.Transcriber.ResolveNameHint is null, "Integrated stop clears optional capture and name resolution");
    }
}
