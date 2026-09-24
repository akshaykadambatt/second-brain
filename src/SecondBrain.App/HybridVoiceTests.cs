using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class HybridVoiceTests
{
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.ScriptEditor.Text = ReaderSession.Sample; await main.ApplyText();
        while (main.Panels.Count < 4) main.AddReader();
        for (var i = 0; i < 4; i++) { main.Panels[i].Width = 350 + i * 100; main.Panels[i].Height = 420; }
        async Task Settle() { for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        await Settle(); main.Playback.StartVoice();
        var flow = main.Playback.Voice;
        var initial = main.Panels.Select(p => p.ScrollPosition).ToArray();
        var version = flow.EvidenceVersion;
        flow.Observe(new(0, 1, "today I want to talk", true, true, .99f), AudioSource.System);
        check(main.Session.Position == 0 && flow.EvidenceVersion == version, "System transcript cannot acquire word position, pace or motion budget");
        var samples = new List<double[]>();
        void Frame(object? sender, EventArgs e) => samples.Add(main.Panels.Select(p => p.ScrollPosition).ToArray());
        CompositionTarget.Rendering += Frame;
        try
        {
            flow.Observe(new(0, 1, "today I want to talk", true, true, .95f));
            await Task.Delay(2400);
            var drift = main.Panels.Select((p, i) => (p.ScrollPosition - initial[i]) / p.LineHeight).ToArray();
            check(main.Session.Position == 5 && main.Panels.All(p => p.SelectedWord == 5), "Four layouts share one microphone-confirmed word identity");
            File.WriteAllText(Path.Combine(directory, "hybrid-settling.json"), JsonSerializer.Serialize(new { frames = samples.Count, holding = flow.Holding, panels = main.Panels.Select((p, i) => new { drift = drift[i], p.AnchorError, p.IsGliding, p.ScrollPosition, p.SelectedWord, p.LineHeight }), samples }));
            check(drift.All(value => value >= 0) && main.Panels.All(p => p.AnchorError <= 1 && !p.IsGliding) && flow.Holding, "Every panel finishes accepted progress on its actual wrapped line without speculative drift");
            var motionLatency = main.Panels.Select(p => p.LastVoiceMotionLatencyMilliseconds).ToArray();
            check(motionLatency.Select((value, i) => drift[i] < .01 || value is > 0 and < 2000).All(v => v), "Panels needing a line change begin visible motion within two seconds; same-line panels stay still");
            var held = main.Panels.Select(p => p.ScrollPosition).ToArray(); await Task.Delay(250);
            check(main.Panels.Select((p, i) => Math.Abs(p.ScrollPosition - held[i]) < .01).All(x => x), "Silence holds all panel positions without lingering drift");
            flow.Observe(new(1, .5, "purple elephants fly away", true, true, .95f)); await Task.Delay(150);
            check(main.Session.Position == 5 && flow.Holding, "Off-script speech leaves the reader held");
            var watch = Stopwatch.StartNew();
            var reacquired = flow.Observe(new(1.5, 1, "about our next steps", true, true, .95f));
            var matchLatency = watch.Elapsed.TotalMilliseconds;
            check(reacquired && main.Session.Position == 9 && matchLatency < 2000, "Distinctive return-to-script phrase reacquires nearby logical position within two seconds");
            await Task.Delay(100);
            Thread.Sleep(220); // Intentional short UI stall: verify discarded catch-up time.
            await Task.Delay(1000);
            var maxStep = 0d; var monotonic = true;
            for (var i = 1; i < samples.Count; i++) for (var panel = 0; panel < 4; panel++)
            {
                var delta = (samples[i][panel] - samples[i - 1][panel]) / main.Panels[panel].LineHeight;
                monotonic &= delta >= -.0001;
                maxStep = Math.Max(maxStep, delta);
            }
            check(monotonic, "Hybrid frames never move backward");
            check(maxStep <= 2.2 / 30 + .001, "Rendering stall never causes a catch-up jump beyond one bounded motion step");
            capture(main.Panels[0], Path.Combine(directory, "hybrid-reader.png"));
            // A layout edit can deliberately re-anchor; it is outside automatic
            // drift measurement and still preserves the selected stable word.
            main.FontSlider.Value = 38; await Settle();
            check(main.Panels.All(p => p.SelectedWord == 9 && p.AnchorError <= 1), "Font changes preserve the selected word under active voice following");
            main.Session.Select(2); await Settle();
            check(flow.Active && flow.Holding && main.Panels.All(p => p.SelectedWord == 2 && p.AnchorError <= 1), "Manual selection resets momentum on every panel while retaining voice tracking");
            flow.Observe(new(3, 1, "want to talk about", true, true, .99f));
            check(main.Session.Position == 6 && flow.Active, "Fresh speech moves the highlight after manual navigation without Resume");
            main.Panels[0].ReaderPlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            check(!flow.Active && !main.Playback.Playing, "Reader Pause voice command stops instead of unexpectedly starting timed scrolling");
            await main.ToggleTimed(); await Task.Delay(200);
            check(main.Playback.Playing && main.Playback.TimedMode && !flow.Active, "Timed fallback remains independently usable after voice pause");
            main.Playback.Pause();
            File.WriteAllText(Path.Combine(directory, "hybrid-measurements.json"), JsonSerializer.Serialize(new { driftLines = drift, firstMotionMs = motionLatency, reacquisitionMatchMs = matchLatency, maxFrameStepLines = maxStep, physicalAudioUsed = false, recognitionLatency = "Not measured by replay; see real Deepgram voice-latency.json separately." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { CompositionTarget.Rendering -= Frame; main.Playback.StopVoice(); }
    }
}
