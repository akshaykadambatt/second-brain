using System.IO;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class SmokeTest
{
    public static async Task Run(MainWindow window, string directory, string phase)
    {
        var checks = new List<string>();
        var unmetTargets = new List<string>();
        try
        {
            async Task Settle()
            {
                for (var i = 0; i < 3; i++) await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            void Check(bool condition, string message)
            { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
            await Settle();
            if (phase == "powerpoint") { await OfficeImportSmokeTests.RunPresentation(window, directory, Check, Capture); }
            else if (phase == "word") { await OfficeImportSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "pdf") { await PdfImportSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "imports") { await ImportSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "meet") { await NameHintSmokeTests.Run(window, directory, Check, meet: true); }
            else if (phase == "names") { await NameHintSmokeTests.Run(window, directory, Check); }
            else if (phase == "capture") { await CaptureSmokeTests.Run(window, directory, Check); }
            else if (phase == "flowing-live") { await FlowingAnswerTests.RunLive(window, directory, Check); }
            else if (phase == "speakers") { await SpeakerSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "rich") { await RichTranscriptTests.Run(window, directory, Check, Capture); }
            else if (phase == "flowing") { await FlowingAnswerTests.Run(window, directory, Check); }
            else if (phase == "opening") { await OpeningTests.Run(window, directory, Check); }
            else if (phase == "latency") { await LatencySmokeTests.Run(window, directory, Check); }
            else if (phase == "context") { await ContextSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "shortcuts") { await ShortcutTests.Run(window, directory, Check, Capture); }
            else if (phase == "chrome") { await ReaderChromeTests.Run(window, directory, Check, Capture); }
            else if (phase == "shell") { await ShellSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "tray") { await TraySmokeTests.Run(window, directory, Check); }
            else if (phase == "wrap") { await WrapAlignmentTests.Run(window, directory, Check, Capture); }
            else if (phase == "storage") { await StorageSmokeTests.Run(window, directory, Check, Capture); }
            else if (phase == "history") { await MaintenanceTests.Run(window, directory, Check, Capture); }
            else if (phase == "history-live") { await MaintenanceTests.RunLive(window, directory, Check); }
            else if (phase == "knowledge") { await KnowledgeTests.Run(window, directory, Check, Capture); }
            else if (phase == "knowledge-live") { await KnowledgeTests.RunLive(window, directory, Check); }
            else if (phase == "companion") { await CompanionTests.Run(window, directory, Check, Capture); }
            else if (phase == "companion-live") { await CompanionTests.RunLive(window, directory, Check); }
            else if (phase == "assistant") { await AssistantTests.Run(window, directory, Check, Capture); }
            else if (phase == "assistant-live") { await AssistantTests.RunLive(window, directory, Check); }
            else if (phase == "hybrid")
            { await HybridVoiceTests.Run(window, directory, Check, Capture); }
            else if (phase == "transcription-live")
            { await TranscriptionTests.RunLive(window, directory, Check); }
            else if (phase == "transcription")
            { await TranscriptionTests.Run(window, directory, Check, Capture); }
            else if (phase == "audio")
            { await AudioRecordingTests.Run(window, directory, Check, Capture); }
            else if (phase == "stream")
            {
                while (window.Panels.Count < 4) window.AddReader();
                for (var i = 0; i < 4; i++) { window.Panels[i].Width = 430 + i * 65; window.Panels[i].Height = 380 + i * 20; }
                var original = window.Session.Text; window.Session.Select(7);
                window.ScriptEditor.Text = "Unapplied draft must survive the incoming answers demo.";
                var draft = window.ScriptEditor.Text;
                var demo = window.OpenStreamDemo(); await Settle();
                var inbox = demo.Inbox;
                var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Stable active answer");
                inbox.Accept(new(answer.RequestId, answer.Id, 0, ReaderSession.Sample + "\n\n"));
                await demo.SelectAnswer(answer); window.Session.Select(12); await Settle();
                var id = window.Session.DocumentId; var words = window.Session.Words.ToArray();
                var y = window.Panels.Select(p => p.WordScreenY(12)).ToArray();
                var offsets = window.Panels.Select(p => p.ScrollPosition).ToArray();
                inbox.Accept(new(answer.RequestId, answer.Id, 1, "This paragraph arrives while the reader is paused.\n\n")); await Settle();
                var pausedError = window.Panels.Select((p, i) => Math.Abs(p.WordScreenY(12) - y[i])).Max();
                Check(pausedError <= 1 && window.Panels.Select((p, i) => p.ScrollPosition == offsets[i]).All(v => v), "Append preserves the paused active word within one DIP on four differently sized panels");
                Check(window.Session.Position == 12 && window.Session.DocumentId == id && words.Zip(window.Session.Words).All(pair => ReferenceEquals(pair.First, pair.Second)), "Append preserves document, block and word identity");
                window.Session.Select(2); await Settle();
                var backY = window.Panels.Select(p => p.WordScreenY(2)).ToArray();
                inbox.Accept(new(answer.RequestId, answer.Id, 2, "A further paragraph arrives while looking back.\n\n")); await Settle();
                Check(window.Panels.Select((p, i) => Math.Abs(p.WordScreenY(2) - backY[i]) <= 1).All(v => v) && !window.Playback.Playing, "Append while scrolled backward preserves every view and does not resume playback");
                var queued = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Queued answer");
                inbox.Accept(new(queued.RequestId, queued.Id, 0, "Queued text never takes over.", AnswerEventKind.Complete)); await Settle();
                Check(window.Session.Answer == answer && window.Panels.All(p => p.DisplayedBlockCount == 5), "A new answer stays queued outside every reader layout");
                await demo.SelectAnswer(queued); await demo.SelectAnswer(answer); await Settle();
                Check(window.Session.Position == 2 && window.Session.DocumentId == id, "Explicit answer navigation restores the prior logical position");
                window.Session.Select(window.Session.Words.Count); await window.ToggleTimed(); await Settle();
                Check(window.Playback.Waiting && window.Session.Answer == answer, "Timed controls keep the streamed answer selected and wait at its open end");
                var cursor = window.Playback.Cursor;
                inbox.Accept(new(answer.RequestId, answer.Id, 3, "Text arriving after a pause should resume at a gentle pace.\n\n"));
                await Task.Delay(350);
                Check(window.Playback.Playing && window.Playback.Cursor > cursor && window.Playback.Cursor < cursor + 1, "New text resumes gently from the previous end without restarting");
                window.Playback.Pause(); await Settle();
                var heldWord = Math.Min(window.Session.Position, window.Session.Words.Count - 1);
                var heldY = window.Panels.Select(p => p.WordScreenY(heldWord)).ToArray();
                inbox.Accept(new(answer.RequestId, answer.Id, 4, "This arrives after pausing partway through a smooth transition.\n\n")); await Settle();
                Check(window.Panels.Select((p, i) => Math.Abs(p.WordScreenY(heldWord) - heldY[i]) <= 1).All(v => v), "Appending after a mid-glide pause does not snap the word back to the reading band");
                window.Session.Select(2); await Settle();
                var historyTimings = new List<double>();
                for (var i = 2; i < 100; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var item = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "History " + i);
                    inbox.Accept(new(item.RequestId, item.Id, 0, "Historical context stays outside the current reader.", AnswerEventKind.Complete));
                    await Settle(); historyTimings.Add(watch.Elapsed.TotalMilliseconds);
                }
                Check(window.Panels.All(p => p.DisplayedBlockCount == 7) && window.Session.Answer == answer, "One hundred retained answers do not grow the active reader layout");
                var appendTimings = new List<double>();
                for (var i = 5; i < 105; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    inbox.Accept(new(answer.RequestId, answer.Id, i, "A stable paragraph keeps earlier words in place as more information arrives for this answer.\n\n"));
                    await Settle(); appendTimings.Add(watch.Elapsed.TotalMilliseconds);
                }
                var appendP95 = appendTimings.Order().ElementAt((int)Math.Ceiling(appendTimings.Count * .95) - 1);
                var historyP95 = historyTimings.Order().ElementAt((int)Math.Ceiling(historyTimings.Count * .95) - 1);
                var firstMean = appendTimings.Take(10).Average(); var lastMean = appendTimings.TakeLast(10).Average();
                var finalError = window.Panels.Select((p, i) => Math.Abs(p.WordScreenY(2) - backY[i])).Max();
                File.WriteAllText(Path.Combine(directory, "stream-measurements.json"), JsonSerializer.Serialize(new { pausedError, finalError, appendP95, historyP95, firstMean, lastMean, appendTimings, historyTimings, words = window.Session.Words.Count, answers = inbox.Answers.Count }, new JsonSerializerOptions { WriteIndented = true }));
                Check(finalError <= 1, "One hundred additional paragraphs preserve the paused active word within one DIP");
                if (appendP95 < 100 && historyP95 < 100 && lastMean < firstMean + 25)
                    checks.Add("Bounded demo history and incremental paragraphs keep p95 UI settle latency below 100 ms without material growth");
                else unmetTargets.Add($"STREAM-006 performance target failed: append p95 {appendP95:F3} ms, history p95 {historyP95:F3} ms; target below 100 ms; first/last mean {firstMean:F3}/{lastMean:F3} ms (growth budget 25 ms).");
                Capture(demo, Path.Combine(directory, "incoming-answers.png")); Capture(window.Panels[0], Path.Combine(directory, "stream-reader.png"));
                demo.Close(); await Settle();
                Check(window.Session.Text == original && window.Session.Position == 7 && window.ScriptEditor.Text == draft, "Closing demo restores the applied script and position without losing the editor draft");
                var liveDemo = window.OpenStreamDemo(); await liveDemo.StartDemo();
                for (var wait = 0; wait < 30 && window.Session.Words.Count == 0; wait++) await Task.Delay(100);
                Check(liveDemo.Inbox.Answers.Count == 3 && window.Session.Answer == liveDemo.Inbox.Answers[0] && window.Session.Words.Count > 0, "User demo starts three queued answers and displays only its first completed paragraph");
                Capture(liveDemo, Path.Combine(directory, "incoming-demo-controls.png"));
                liveDemo.Close(); await Settle();
                Check(!window.Voice.Running && !window.Playback.Playing, "Stream demo tests leave microphone and playback stopped");
            }
            else if (phase == "replay")
            {
                window.ReplayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(8000);
                Check(window.Session.Position == 15 && window.HeardText.Text.StartsWith("Demo heard:", StringComparison.Ordinal), "Dummy speech replay advances 15 words through shared reader interfaces");
                Check(!window.Voice.Running && !window.Playback.Playing, "Replay uses neither microphone nor the timed clock");
                window.ReplayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(600);
                window.Session.Select(0); await Task.Delay(1200);
                Check(window.Session.Position == 0, "Manual reset stops later dummy events until explicitly restarted");
            }
            else if (phase == "study")
            {
                var script = window.Session.Text; var style = window.Session.Style;
                window.ScriptEditor.Text = "Unapplied editor draft retained across a reading comparison.";
                var draft = window.ScriptEditor.Text;
                var study = new ReadingStudyWindow(window, Path.Combine(directory, "reading-study")) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                study.Show(); await Settle();
                await study.VerifyPersistence(Check); await Settle();
                Capture(study, Path.Combine(directory, "reading-comparison.png"));
                study.Close(); await Settle();
                Check(window.Session.Text == script && window.ScriptEditor.Text == draft && window.Session.Style == style,
                    "Closing comparison restores applied script, unapplied draft and reader appearance");
                Check(!window.Voice.Running && !window.Playback.Playing, "Reading comparison tests leave microphone and playback stopped");
            }
            else if (phase == "timed")
            {
                while (window.Panels.Count < 4) window.AddReader();
                await Settle();
                await window.ToggleTimed();
                await Task.Delay(2000);
                Check(window.Playback.Cursor > 3 && window.Session.Position > 2 && !window.Voice.Running, "Timed playback advances shared word position without microphone");
                Check(window.Panels.All(p => p.ScrollPosition > 1 && p.SelectedWord == window.Session.Position), "All panels scroll continuously between word boundaries");
                var stop = System.Diagnostics.Stopwatch.StartNew();
                window.Playback.Pause();
                var offsets = window.Panels.Select(p => p.ScrollPosition).ToArray();
                await Task.Delay(75);
                Check(window.Panels.Select((p, i) => Math.Abs(p.ScrollPosition - offsets[i]) < .01).All(x => x), "Pause freezes every panel immediately and remains stationary after 75 ms");
                var pausedCursor = window.Playback.Cursor;
                await window.ToggleTimed(); await Task.Delay(300);
                Check(window.Playback.Cursor > pausedCursor && window.Session.Position >= (int)pausedCursor, "Resume continues from the same logical position");
                window.Session.Select(window.Session.Position);
                Check(!window.Playback.Playing, "Clicking the already-selected word still pauses automatic movement");
                window.Playback.Sentence(1); await Settle();
                Check(window.Session.Position == 9 && !window.Playback.Playing, "Next sentence pauses and selects the next sentence start");
                window.Playback.Sentence(-1); await Settle();
                Check(window.Session.Position == 0, "Previous sentence recovers the prior sentence start");
                window.SpeedSlider.Value = 210;
                await window.ToggleTimed(); await Task.Delay(100); window.Playback.UseVoice();
                var held = window.Panels.Select(p => p.ScrollPosition).ToArray();
                await Task.Delay(100);
                Check(!window.Playback.Playing && window.Panels.Select((p, i) => Math.Abs(p.ScrollPosition - held[i]) < .01).All(x => x), "Switching to voice mode clears timed momentum");
                Capture(window, Path.Combine(directory, "timed-controls.png"));
                Capture(window.Panels[0], Path.Combine(directory, "timed-reader.png"));
            }
            else if (phase == "flow")
            {
                while (window.Panels.Count < 4) window.AddReader();
                for (var i = 0; i < 4; i++) { window.Panels[i].Width = 430 + i * 50; window.Panels[i].Height = 380 + i * 20; }
                window.Session.Select(0);
                await Settle();
                var panels = window.Panels.ToArray();
                var initial = panels.Select(p => p.ScrollPosition).ToArray();
                window.Session.Select(1, fromVoice: true);
                await Settle(); await Task.Delay(150);
                Check(panels.Select((p, i) => Math.Abs(p.ScrollPosition - initial[i]) < .05).All(x => x), "Words within the same line do not move the text");
                var nextLine = panels.Max(p => Enumerable.Range(1, window.Session.Words.Count - 1).First(i => p.WordLine(i) > p.WordLine(0) + 1));
                var samples = new List<(double Time, double[] Offsets)>();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                void Frame(object? sender, EventArgs args) => samples.Add((timer.Elapsed.TotalSeconds, panels.Select(p => p.ScrollPosition).ToArray()));
                CompositionTarget.Rendering += Frame;
                try
                {
                    window.Session.Select(nextLine, fromVoice: true);
                    await Settle();
                    Check(panels.Select((p, i) => Math.Abs(p.ScrollPosition - initial[i]) < p.LineHeight * .25).All(x => x), "Crossing a line starts a glide instead of an immediate line jump");
                    await Task.Delay(2600);
                    Check(samples.Count > 20, "Actual WPF render frames were observed: " + samples.Count);
                    for (var frame = 1; frame < samples.Count; frame++)
                    {
                        var dt = samples[frame].Time - samples[frame - 1].Time;
                        for (var panel = 0; panel < panels.Length; panel++)
                        {
                            var delta = samples[frame].Offsets[panel] - samples[frame - 1].Offsets[panel];
                            if (delta < -.01 || delta > panels[panel].LineHeight * 2.2 * Math.Min(dt + .008, .042) + .05)
                                throw new InvalidOperationException($"Unexpected glide displacement: {delta:F3} DIP in {dt:F4}s");
                        }
                    }
                    Check(panels.All(p => p.AnchorError <= 1 && !p.IsGliding), "Four panel glides settle within one DIP of the reading band");
                }
                finally { CompositionTarget.Rendering -= Frame; }
                File.WriteAllText(Path.Combine(directory, "flow-frames.json"), JsonSerializer.Serialize(samples.Select(s => new { seconds = s.Time, offsets = s.Offsets })));
                var held = panels.Select(p => p.ScrollPosition).ToArray();
                await Task.Delay(250);
                Check(panels.Select((p, i) => p.ScrollPosition == held[i]).All(x => x), "No drift after speech progress stops");
                window.Session.Select(nextLine + 12, fromVoice: true); await Settle(); await Task.Delay(100);
                window.FontSlider.Value = 38; panels[0].Width += 35;
                await Settle();
                Check(panels.All(p => p.SelectedWord == nextLine + 12 && p.AnchorError <= 1 && !p.IsGliding), "Resize and font changes preserve the selected word and cancel stale glide geometry");
                window.Session.Select(nextLine + 20, fromVoice: true); await Settle(); await Task.Delay(100);
                window.Session.Select(0); await Settle(); await Task.Delay(150);
                Check(panels.All(p => p.SelectedWord == 0 && p.AnchorError <= 1 && !p.IsGliding), "Manual reposition immediately clears voice momentum");
                Capture(panels[0], Path.Combine(directory, "reader-flow.png"));
                Check(!window.Voice.Running, "Flow tests use dummy progress without microphone or network");
            }
            else if (phase == "voice")
            {
                var keys = new ApiKeyStore(directory);
                await window.Voice.StartAsync("missing-device");
                Check(!window.Voice.Running && window.StatusText.Text.Contains("key is not configured", StringComparison.Ordinal), "Missing credential is visible without opening the microphone");
                var import = Path.Combine(directory, "temporary-test-key.txt");
                const string fakeKey = "synthetic-test-credential-not-a-real-key";
                File.WriteAllText(import, fakeKey);
                keys.Import(import); File.Delete(import);
                Check(keys.Load() == fakeKey && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(keys.FilePath)).Contains(fakeKey, StringComparison.Ordinal), "Key roundtrips with Windows user protection and is absent from plaintext storage");
                File.Delete(keys.FilePath);
                Check(window.MicrophonePicker.Items.Cast<Microphone>().All(m => !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.Name)), "Listed microphones have selectable endpoint IDs and readable names");
                await window.Voice.StopAsync(); await window.Voice.StopAsync();
                Check(!window.Voice.Running, "Repeated stop is safe");
            }
            else if (phase == "deepgram")
            {
                const string sentence = "Today I want to talk about our next steps.";
                window.ScriptEditor.Text = sentence;
                await window.ApplyText();
                window.AddReader();
                var wave = Path.Combine(directory, "synthetic-speech.wav");
                await Task.Run(() =>
                {
                    using var synth = new SpeechSynthesizer();
                    var voice = synth.GetInstalledVoices().First(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
                    synth.SelectVoice(voice.VoiceInfo.Name);
                    synth.SetOutputToWaveFile(wave); synth.Speak(sentence);
                });
                var completed = new TaskCompletionSource();
                window.Voice.Stopped += () => completed.TrySetResult();
                await window.Voice.StartAsync(waveFile: wave);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Check(window.Session.Position >= 8, "Live Deepgram WAV recognition advances through at least eight script words: " + window.HeardText.Text + "; status=" + window.StatusText.Text);
                using (var latency = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "voice-latency.json"))))
                {
                    var samples = latency.RootElement.GetProperty("samples").EnumerateArray().ToArray();
                    Check(samples.Any(s => !s.GetProperty("Final").GetBoolean() && s.GetProperty("ApproximateInterimLagMs").ValueKind == JsonValueKind.Number)
                        && samples.All(s => s.GetProperty("MatchMs").GetDouble() >= 0), "Real interim recognition lag is recorded separately from dispatcher and matcher timing");
                }
                File.WriteAllText(Path.Combine(directory, "voice-animation.json"), JsonSerializer.Serialize(new { firstMotionMs = window.Panels.Select(p => p.LastVoiceMotionLatencyMilliseconds).ToArray(), note = "WPF first visible movement after accepted speech; separate from provider lag. Hidden windows, not physical screen latency." }));
                await window.StopListening();
                Check(!window.Voice.Running, "Deepgram stops and releases input");
                var restart = window.Voice.StartAsync(waveFile: wave);
                var stopTime = System.Diagnostics.Stopwatch.StartNew();
                await window.StopListening(); await restart;
                Check(!window.Voice.Running && stopTime.Elapsed.TotalSeconds < 3, "Stopping during connection cancels startup without a stale session");
            }
            else
            {
                if (phase == "verify")
                {
                    Check(window.Panels.Count == 4, "Four panel layouts restore across process restart");
                    Check(window.Settings.ReaderStyle.FontSize == 38, "Appearance survives process restart");
                    Check(window.Settings.TimedWordsPerMinute == 175 && window.SpeedSlider.Value == 175, "Timed WPM survives process restart");
                    Check(window.ScriptEditor.Text == ReaderSession.Sample, "Script survives process restart");
                    Check(window.MicrophonePicker.Items.Count == 0 || window.MicrophonePicker.SelectedItem is Microphone mic && mic.Id == window.Settings.MicrophoneId, "Selected microphone survives process restart");
                }
                window.OpenReaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var first = window.Panels[0];
                var count = window.Panels.Count;
                window.OpenReaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.Panels.Count == count, "Open focuses an existing panel");
                while (window.Panels.Count < 4) window.AddPanelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var i = 0; i < window.Panels.Count; i++) { window.Panels[i].Width = 430 + 50 * i; window.Panels[i].Height = 380 + 20 * i; }
                window.Session.Select(35);
                window.FontSlider.Value = 38;
                window.SpeedSlider.Value = 175;
                await Settle();
                Check(window.Panels.All(p => p.SelectedWord == 35 && p.Topmost), "Four differently sized panels share the selected word");
                Check(window.Panels.All(p => p.AnchorError <= 1.1), "Selected word stays on the reading band after reflow: " + string.Join(", ", window.Panels.Select(p => p.AnchorError.ToString("F2"))));
                Check(window.Panels.All(p => p.CaptureExcluded || p.CaptureText.Text.Contains("failed", StringComparison.Ordinal)), "Capture API result is verified or visibly reported as failed");
                NativeWindows.Restore(first, new PanelPlacement(80000, 80000, 500, 380));
                var recovered = NativeWindows.GetBounds(first);
                Check(recovered.Left < 80000 && recovered.Top < 80000, "Offscreen saved geometry recovers to a connected display");
                await Settle();
                var document = window.Session.DocumentId;
                window.ScriptEditor.Text = " ";
                Check(!await window.ApplyText() && window.Session.DocumentId == document, "Blank input preserves the current script");
                window.ScriptEditor.Text = ReaderSession.Sample;
                await Settle();
                Check(window.WorkspaceGrid.ActualWidth <= window.WorkspaceScroll.ViewportWidth + 1, "Editor and appearance controls fit the normal window width");
                var statusBottom = window.StatusText.TranslatePoint(new Point(0, window.StatusText.ActualHeight), window.WorkspaceScroll).Y;
                Check(statusBottom <= window.WorkspaceScroll.ViewportHeight + 1, "Recognition status is visible without scrolling at the normal window size");
                var originalWidth = window.Width; var originalHeight = window.Height;
                window.Width = 680; window.Height = 520;
                await Settle();
                Check(window.WorkspaceScroll.ScrollableHeight > 0, "Small viewports retain access to controls through scrolling");
                window.Width = originalWidth; window.Height = originalHeight;
                await Settle();
                Capture(window, Path.Combine(directory, "control-window.png"));
                Capture(first, Path.Combine(directory, "reader-window.png"));
                first.Close();
                Check(window.Panels.Count == 3, "Individual panel closes without affecting siblings");
                window.AddReader();
                window.RememberPosition.IsChecked = true;
                Check(!window.Voice.Running, "Microphone is inactive unless explicitly started");
            }
            await window.StopListening();
            var open = window.Panels.ToArray();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.ExitApplication();
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!window.ShutdownCompleted) await Task.Delay(20, deadline.Token);
            Check(open.All(p => !p.IsVisible) && !window.TrayVisible, "Explicit exit closes every reader and removes the tray icon");
            if (phase == "tray") Check(window.Companion?.Active == false && !window.Recorder.HasSession && window.Recorder.State == RecordingState.Completed,
                "Tray Exit completes active synthetic recording and companion shutdown before process exit");
            File.WriteAllText(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(new { passed = unmetTargets.Count == 0, functionalPassed = true, checks, unmetTargets }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString() }));
            Application.Current.Shutdown(1);
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var rect = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(window.Background, null, rect);
            context.DrawRectangle(new VisualBrush(content), null, rect);
        }
        image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}
