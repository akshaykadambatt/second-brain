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
            capture.Dispose(); capture.Dispose(); var after = count; await Task.Delay(700);
            check(count == after, "Repeated stop disposes capture and prevents late frame delivery");
            fixture.Close(); check(!target.Available, "Closed target invalidates its window identity");
            check(!Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Any(), "Capture writes no frame image files");
        }
        finally { capture?.Dispose(); fixture.Close(); }
    }
}
