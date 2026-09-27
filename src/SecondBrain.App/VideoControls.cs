using System.Diagnostics;
using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim videoCommands = new(1);
    private bool videoBusy;
    internal VideoRecording? VideoRecording { get; private set; }
    private async void RecordMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (VideoRecording?.Active == true) { await StopVideo(); return; }
        var picker = new DisplayPicker(this);
        if (picker.ShowDialog() == true) await StartVideo(picker.Selection);
    }
    internal async Task<bool> StartVideo(VideoSelection? selection, IAnswerProvider? provider = null,
        Func<DisplayChoice, Action<DisplayFrame>, Action<string>, IDisplayCapture>? captureFactory = null)
    {
        if (selection is null) return false;
        await videoCommands.WaitAsync(); videoBusy = true; RefreshVideoControls();
        try
        {
            if (closing || VideoRecording?.Active == true) return false;
            _ = selection.Size;
            if (captureFactory is null && !DisplayChoice.List().Contains(selection.Display)) throw new InvalidOperationException("Display changed. Choose it again.");
            if (Companion?.Active != true && !await StartCompanion(provider)) { VideoStatus.Text = "Video did not start. " + CompanionStatus.Text; return false; }
            if (closing || Recorder.State != RecordingState.Recording || Recorder.LastDirectory is not { } directory) return false;
            VideoRecording = new(directory, selection, Recorder.ClockOrigin, captureFactory, Recorder);
            VideoStatus.Text = "Starting display recording…";
            await VideoRecording.Ready;
            if (closing || Recorder.State != RecordingState.Recording) { await VideoRecording.Stop(); return false; }
            VideoStatus.Text = "Recording display · " + selection.Size.Width + " × " + selection.Size.Height + " · 30 fps · microphone + computer audio";
            return true;
        }
        catch (Exception ex)
        {
            if (VideoRecording is { } recording) await recording.Stop();
            VideoStatus.Text = "Video did not start: " + (ex is IOException or InvalidOperationException ? ex.Message : "Display or encoder unavailable.") + " Listening can continue.";
            log.Write("Video startup failure=" + ex.GetType().Name); return false;
        }
        finally { videoBusy = false; videoCommands.Release(); RefreshVideoControls(); RefreshCompanionControls(); }
    }
    internal async Task StopVideo()
    {
        await videoCommands.WaitAsync(); videoBusy = true; RefreshVideoControls();
        try
        {
            if (VideoRecording is not { } recording) return;
            await recording.Stop();
            VideoStatus.Text = recording.Error is null ? "Video saved · " + System.IO.Path.GetFileName(recording.Path) : "Video stopped: " + recording.Error;
        }
        finally { videoBusy = false; videoCommands.Release(); RefreshVideoControls(); RefreshCompanionControls(); }
    }
    private void RefreshVideoControls()
    {
        if (!initialized) return;
        RecordMeetingButton.IsEnabled = !videoBusy && !companionBusy && !closing;
        RecordMeetingButton.Content = videoBusy ? "Saving video…" : VideoRecording?.Active == true ? "Stop video" : "Record meeting…";
        OpenVideoButton.IsEnabled = VideoRecording is { Active: false, Error: null, FrameCount: > 0 };
    }
    private void TickVideo()
    {
        if (VideoRecording is null || videoBusy) return;
        if (VideoRecording.Active && Recorder.State is RecordingState.Completed or RecordingState.Paused or RecordingState.Failed) _ = StopVideo();
        else if (!VideoRecording.Active && VideoRecording.Error is { } error) { VideoStatus.Text = "Video stopped: " + error + " Audio is separate."; RefreshVideoControls(); }
    }
    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        try { if (VideoRecording is { Active: false } recording && File.Exists(recording.Path)) Process.Start(new ProcessStartInfo(recording.Path) { UseShellExecute = true }); }
        catch (Exception) { VideoStatus.Text = "Could not open the video. Open the meeting's recording folder instead."; }
    }
}
