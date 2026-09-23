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
        try
        {
            async Task Settle()
            {
                for (var i = 0; i < 3; i++) await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            void Check(bool condition, string message)
            { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
            await Settle();
            if (phase == "voice")
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
            window.Close();
            Check(open.All(p => !p.IsVisible), "Closing main window closes every reader");
            File.WriteAllText(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
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
