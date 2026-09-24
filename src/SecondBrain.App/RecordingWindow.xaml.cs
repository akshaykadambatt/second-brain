using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class RecordingWindow : Window
{
    private sealed record SessionItem(string Path, string Label);
    private readonly MainWindow main;
    private readonly RecordingService recorder;
    private readonly string root;
    private readonly bool hidden;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool closing, allowClose, recovering;
    internal Task RecoveryTask { get; private set; } = Task.CompletedTask;
    internal RecordingWindow(MainWindow main, string root, bool hidden)
    {
        this.main = main; recorder = main.Recorder; this.root = root; this.hidden = hidden; Owner = main;
        InitializeComponent();
        if (hidden) { Opacity = 0; ShowActivated = false; ShowInTaskbar = false; }
        RefreshDevices(); RefreshSessions(); Update();
        recorder.Changed += RecordingChanged; timer.Tick += (_, _) => UpdateMeters(); timer.Start();
        Closed += (_, _) => { timer.Stop(); recorder.Changed -= RecordingChanged; };
    }
    private void RecordingChanged() { Update(); if (!recorder.HasSession) RefreshSessions(); }
    private void RefreshDevices()
    {
        try
        {
            var micId = (MicrophonePicker.SelectedItem as AudioDevice)?.Id ?? main.Settings.RecordingMicrophoneId ?? main.Settings.MicrophoneId;
            var outputId = (OutputPicker.SelectedItem as AudioDevice)?.Id ?? main.Settings.RecordingOutputId;
            var inputs = hidden ? new AudioDevice[] { new("test-mic", "Synthetic microphone (test only)") } : AudioCaptureSource.Devices(AudioSource.Microphone);
            var outputs = hidden ? new AudioDevice[] { new("test-output", "Synthetic output (test only)") } : AudioCaptureSource.Devices(AudioSource.System);
            MicrophonePicker.ItemsSource = inputs; OutputPicker.ItemsSource = outputs;
            MicrophonePicker.SelectedItem = inputs.FirstOrDefault(d => d.Id == micId) ?? (micId is null ? inputs.FirstOrDefault() : null);
            OutputPicker.SelectedItem = outputs.FirstOrDefault(d => d.Id == outputId) ?? (outputId is null ? outputs.FirstOrDefault() : null);
            if (hidden) { MicrophonePicker.SelectedIndex = 0; OutputPicker.SelectedIndex = 0; }
            if (MicrophonePicker.SelectedItem is null || OutputPicker.SelectedItem is null) StatusText.Text = "Select both devices. A saved device may be disconnected.";
        }
        catch (Exception ex) { StatusText.Text = "Devices could not be listed: " + ex.Message; }
    }
    private void Update()
    {
        var locked = recorder.HasSession || recorder.Busy || recovering || closing;
        StartButton.IsEnabled = !locked;
        MicrophonePicker.IsEnabled = OutputPicker.IsEnabled = RefreshButton.IsEnabled = !locked;
        PauseButton.IsEnabled = !recovering && recorder.State is RecordingState.Recording or RecordingState.Paused;
        PauseButton.Content = recorder.State == RecordingState.Paused ? "Resume" : "Pause";
        StopButton.IsEnabled = recorder.HasSession && !recorder.Busy && !recovering;
        RecoverButton.IsEnabled = !locked; StatusText.Text = recorder.Message;
        if (recorder.State is RecordingState.Idle or RecordingState.Completed && (MicrophonePicker.SelectedItem is null || OutputPicker.SelectedItem is null))
            StatusText.Text = "Select both devices. A saved device may be disconnected.";
        LocationText.Text = recorder.LastDirectory ?? root; UpdateMeters();
    }
    private void UpdateMeters()
    {
        var mic = recorder.Meter(AudioSource.Microphone); var system = recorder.Meter(AudioSource.System);
        MicrophoneMeter.Value = mic.Level; MicrophoneStatus.Text = mic.Status;
        OutputMeter.Value = system.Level; OutputStatus.Text = system.Status;
        ClockText.Text = TimeSpan.FromSeconds(recorder.Elapsed).ToString(@"hh\:mm\:ss");
    }
    internal async Task StartRecording()
    {
        if (MicrophonePicker.SelectedItem is not AudioDevice mic || OutputPicker.SelectedItem is not AudioDevice output)
        { StatusText.Text = "Select an available microphone and computer-audio output first."; return; }
        main.SaveRecordingDevices(mic.Id, output.Id);
        await recorder.StartAsync(mic.Id, output.Id);
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await StartRecording();
    private async void Pause_Click(object sender, RoutedEventArgs e)
    { if (recorder.State == RecordingState.Paused) await recorder.ResumeAsync(); else await recorder.PauseAsync(); }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await recorder.StopAsync();
    private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshDevices(); RefreshSessions(); }
    private void RefreshSessions()
    {
        var items = new List<SessionItem>();
        try
        {
            if (Directory.Exists(root)) foreach (var folder in Directory.EnumerateDirectories(root).OrderDescending().Take(20))
            {
                try { var manifest = RecordingSession.ReadManifest(folder); items.Add(new(folder, $"{manifest.StartedUtc.ToLocalTime():g} · {manifest.State} · {TimeSpan.FromSeconds(manifest.DurationSeconds):hh\\:mm\\:ss}")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { items.Add(new(folder, Path.GetFileName(folder) + " · manifest needs attention")); }
            }
            SessionsList.ItemsSource = items;
            SessionsList.SelectedItem = items.FirstOrDefault(i => i.Path == recorder.LastDirectory) ?? items.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "Recording history is unavailable: " + ex.Message; }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var path = (SessionsList.SelectedItem as SessionItem)?.Path ?? recorder.LastDirectory ?? root;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true, Arguments = "\"" + path + "\"" });
        }
        catch (Exception ex) { StatusText.Text = "Could not open the folder: " + ex.Message; }
    }
    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (recorder.HasSession || recorder.Busy || recovering) return;
        if (SessionsList.SelectedItem is not SessionItem item) { StatusText.Text = "Select an interrupted session first."; return; }
        recovering = true; Update(); StatusText.Text = "Recovering available chunks…";
        try { RecoveryTask = Task.Run(() => RecordingSession.Recover(item.Path)); await RecoveryTask; RefreshSessions(); StatusText.Text = "Recovery complete. Open the recording folder to play the two tracks."; }
        catch (Exception ex) { StatusText.Text = "Recovery could not finish; existing chunks are retained. " + ex.Message; }
        finally { recovering = false; StartButton.IsEnabled = RecoverButton.IsEnabled = true; MicrophonePicker.IsEnabled = OutputPicker.IsEnabled = RefreshButton.IsEnabled = true; }
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (recovering) { e.Cancel = true; StatusText.Text = "Wait for recovery to finish before closing."; return; }
        if (allowClose || !recorder.HasSession) { base.OnClosing(e); return; }
        e.Cancel = true; if (closing) return; closing = true; Update();
        await recorder.StopAsync(); allowClose = true; await Dispatcher.InvokeAsync(Close);
    }
}
