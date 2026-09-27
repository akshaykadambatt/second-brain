using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class VideoResilienceSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        await Task.Factory.StartNew(() =>
        {
            var path = Path.Combine(directory, "video-interrupted.mp4");
            using (var writer = new NativeVideoWriter(path, 320, 180))
            { var pixels = new byte[320 * 180 * 4]; Array.Fill(pixels, (byte)120); for (var i = 0; i < 90; i++) writer.Write(pixels, i); writer.Complete(); }
            using (var tail = new FileStream(path, FileMode.Append)) tail.Write([0, 0, 0]);
            var bytes = File.ReadAllBytes(path); var recovered = VideoRecovery.Recover(directory, Path.GetFileName(path));
            var decoded = VideoSmokeTests.Decode(recovered.Path);
            check(decoded.Count == 90 && recovered.Prefix.DiscardedBytes >= 3 && File.ReadAllBytes(path).SequenceEqual(bytes), "Native decoder plays recovered complete fragments while the damaged original stays byte-identical");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var selection = new VideoSelection(new(1, 320, 180, "Synthetic display"), false);
        var low = new VideoRecording(directory, selection, AudioClock.Now, (_, receive, _) => new VideoSmokeTests.Frames(receive), freeBytes: () => 0);
        var refused = false; try { await low.Ready; } catch (IOException) { refused = true; } await low.Stop();
        check(refused && low.FrameCount == 0 && low.Error!.Contains("512 MB"), "Insufficient disk space refuses video before capture or encoding starts");
        var injected = new VideoRecording(directory, selection, AudioClock.Now, (_, receive, _) => new VideoSmokeTests.Frames(receive), beforeFrame: f => { if (f == 8) throw new IOException("Injected encoder failure"); });
        await injected.Ready; await Wait(injected); await injected.Stop();
        check(injected.FrameCount == 8 && injected.Error!.Contains("Injected") && (await Task.Run(() => VideoSmokeTests.Decode(injected.Path))).Count == 8, "Encoder failure retains accepted frames and attempts native finalization");
        var checks = 0;
        var pressure = new VideoRecording(directory, selection, AudioClock.Now, (_, receive, _) => new VideoSmokeTests.Frames(receive), freeBytes: () => Interlocked.Increment(ref checks) < 3 ? long.MaxValue : 0);
        await pressure.Ready; await Wait(pressure); await pressure.Stop();
        check(pressure.Error!.Contains("Low disk") && pressure.FrameCount == 30 && (await Task.Run(() => VideoSmokeTests.Decode(pressure.Path))).Count == 30, "Disk pressure stops at the reserve and closes the existing video");
        Action<string>? disconnect = null;
        var display = new VideoRecording(directory, selection, AudioClock.Now, (_, receive, failed) => { disconnect = failed; return new VideoSmokeTests.Frames(receive); });
        await display.Ready; disconnect!("Selected display disconnected."); await display.Stop();
        check(display.Error!.Contains("disconnected") && !display.Active && !main.Recorder.HasSession, "Display removal releases video without opening another audio capture");
        var view = new VideoLibraryView(main, directory);
        var window = new Window { Owner = main, Width = 720, Height = 460, Content = view, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; window.Show();
        try
        {
            check(view.Clips.Items.Count >= 3 && view.Clips.Items.Cast<SavedVideo>().Any(v => v.State == "RecoveredPartial"), "Saved video view lists normal, failed and recovered clips");
            view.Clips.SelectedItem = view.Clips.Items.Cast<SavedVideo>().Single(v => System.IO.Path.GetFileName(v.Path) == "video-interrupted.mp4");
            await view.RecoverSelected();
            check(view.Status.Text.Contains("Recovered copy saved") && !main.Recorder.HasSession, "Explicit recovery UI preserves originals and never starts listening");
            capture(window, Path.Combine(directory, "video-recovery.png"));
        }
        finally { window.Close(); }
    }
    private static async Task Wait(VideoRecording video)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (video.Active) await Task.Delay(20, timeout.Token); }
}
