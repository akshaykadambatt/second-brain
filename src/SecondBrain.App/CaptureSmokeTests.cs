using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SecondBrain.App;

internal static class CaptureSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check)
    {
        var fixture = new Window { Title = "Synthetic meeting capture fixture", Width = 600, Height = 340,
            Left = 40, Top = 40, ShowActivated = false, ShowInTaskbar = true,
            Background = Brushes.Chartreuse, Content = new TextBlock { Text = "Synthetic participant", FontSize = 32 } };
        IMeetingCapture? capture = null;
        try
        {
            fixture.Show(); await fixture.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var handle = new WindowInteropHelper(fixture).Handle;
            var target = MeetingWindow.List(true).Single(w => w.Handle == handle);
            check(target.Available && !MeetingWindow.List().Any(w => w.Handle == handle), "Picker excludes own app windows; synthetic target identity is valid");
            var received = new TaskCompletionSource<MeetingFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0; var nonzero = false;
            capture = new MeetingWindowCapture(target, frame => { nonzero = frame.Pixels.Any(b => b != 0); Interlocked.Increment(ref count); received.TrySetResult(frame); }, reason => received.TrySetException(new InvalidOperationException(reason)));
            var observed = await received.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await Task.Delay(100);
            check(nonzero && observed.Width > 0 && observed.Height > 0, "Real Windows Graphics Capture reads only the explicitly selected synthetic window");
            check(observed.Pixels.All(b => b == 0), "Managed frame pixels are erased after ephemeral local processing");
            var created = 0; var stopped = 0; Action<MeetingFrame>? callback = null; Action<string>? failure = null;
            using var visuals = new MeetingVisuals(main.Dispatcher);
            visuals.Factory = (w, f, e) => { created++; callback = f; failure = e; return new FakeCapture(() => stopped++); };
            visuals.Select(target); visuals.Enable(true);
            check(created == 0 && !visuals.Listening, "Selection and opt-in never start audio or capture while idle");
            visuals.Listen(true); check(created == 1 && visuals.Running, "Capture starts only with listening and explicit opt-in");
            visuals.Select(null); check(created == 1 && stopped == 0 && visuals.Target == target, "Cancelling window selection leaves active capture untouched");
            var oldCallback = callback!; visuals.Clear(); oldCallback(new(DateTimeOffset.UtcNow, 1, 1, [200, 200, 200, 255]));
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(!visuals.Running && visuals.LastFrame is null && visuals.Listening, "Clearing selection discards late frames and leaves listening active");
            visuals.Select(target); visuals.Enable(true); failure!("Synthetic permission failure");
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(!visuals.Running && visuals.Listening && visuals.Status.Contains("Audio-only"), "Capture failure visibly falls back without stopping listening");
            visuals.Enable(true); visuals.Listen(false);
            check(!visuals.Running && !visuals.Listening && stopped == created, "Stopping listening disposes every capture instance");
            capture.Dispose(); capture.Dispose(); var after = count; await Task.Delay(700);
            check(count == after, "Repeated stop disposes capture and prevents late frame delivery");
            fixture.Close(); check(!target.Available, "Closed target invalidates its window identity");
            check(!Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Any(), "Capture writes no frame image files");
        }
        finally { capture?.Dispose(); fixture.Close(); }
    }
    private sealed class FakeCapture(Action stop) : IMeetingCapture { public void Dispose() => stop(); }
}
