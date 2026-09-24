using System.Windows;
using System.Windows.Threading;
using System.IO;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class TraySmokeTests
{
    private sealed class OfflineProvider : IAnswerProvider
    {
        public Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation) => Task.CompletedTask;
    }

    internal static async Task Run(MainWindow window, string directory, Action<bool, string> check)
    {
        window.InitializeTray();
        check(window.TrayVisible && !window.ShowInTaskbar, "Controls have a tray icon and no taskbar item");
        while (window.Panels.Count < 4) window.AddReader();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(window.Panels.All(p => !p.ShowInTaskbar && NativeWindows.IsToolWindow(p)), "All four readers use tool-window styles without taskbar or Alt+Tab entries");
        check(window.Panels.All(p => p.Topmost && p.CaptureExcluded), "Reader topmost and capture exclusion survive tool-window styling");
        window.Session.Select(4);
        window.Panels[0].Width += 40;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(window.Panels.All(p => p.SelectedWord == 4), "Manual selection and resize preserve shared reader position");
        await window.ToggleTimed();
        var cursor = window.Playback.Cursor;
        window.Close();
        await Task.Delay(300);
        check(!window.IsVisible && !window.ShutdownCompleted && window.TrayVisible, "Closing controls hides them and keeps the tray alive");
        check(window.Panels.All(p => p.IsVisible) && window.Playback.Playing && window.Playback.Cursor > cursor,
            "Readers and active playback continue after controls are closed");
        window.ShowControls();
        check(window.IsVisible && !window.ShowInTaskbar && window.Panels.Count == 4, "Tray restore reopens controls without a taskbar item or duplicate readers");
        window.WindowState = WindowState.Minimized;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(!window.IsVisible && window.Playback.Playing, "Minimizing controls hides them without pausing playback");
        window.ShowControls();
        check(window.IsVisible && window.WindowState == WindowState.Normal, "Tray restore recovers minimized controls");
        check(window.Panels.All(p => !p.ShowInTaskbar && NativeWindows.IsToolWindow(p) && p.CaptureExcluded),
            "Reader shell styles and capture exclusion survive control hide/show cycles");
        check(!window.Voice.Running && !window.Recorder.HasSession, "Tray actions never start microphone or recording implicitly");
        var keyPath = Path.Combine(directory, "synthetic-key.txt");
        File.WriteAllText(keyPath, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(keyPath); } finally { File.Delete(keyPath); }
        check(await window.StartCompanion(new OfflineProvider()), "Explicit test start opens the isolated synthetic companion");
        var elapsed = window.Recorder.Elapsed;
        window.Close();
        await Task.Delay(300);
        check(!window.IsVisible && window.Companion?.Active == true && window.Recorder.State == RecordingState.Recording
            && window.Recorder.Elapsed > elapsed && window.Panels.All(p => p.IsVisible),
            "Closing controls during a session keeps synthetic capture, companion and readers running");
        // Leave the session active so the common explicit-exit assertions exercise stop/save.
    }
}
