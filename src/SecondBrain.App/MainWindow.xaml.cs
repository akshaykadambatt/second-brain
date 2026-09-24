using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly DiagnosticLog log;
    private AppSettings settings;
    private bool initialized, closing, allowClose, startingVoice;
    private readonly bool hiddenTestMode;
    private readonly DispatcherTimer saveTimer;
    private readonly List<ReaderWindow> panels = [];
    private readonly string dataDirectory;
    private ReadingStudyWindow? study;
    internal StreamDemoWindow? StreamDemo { get; private set; }
    private ScriptedSpeechSource? replay;
    internal ReaderSession Session { get; } = new();
    internal ReaderPlayback Playback { get; }
    private readonly System.Diagnostics.Stopwatch playbackClock = System.Diagnostics.Stopwatch.StartNew();
    private double playbackFrame;
    internal VoiceService Voice { get; }
    internal RecordingService Recorder { get; }
    private RecordingWindow? recordingWindow;
    internal IReadOnlyList<ReaderWindow> Panels => panels;
    internal AppSettings Settings => settings;

    public MainWindow(SettingsStore store, AppSettings settings, DiagnosticLog log, string dataDirectory, bool hiddenTestMode = false)
    {
        this.store = store; this.settings = settings; this.log = log;
        this.dataDirectory = dataDirectory;
        InitializeComponent();
        Playback = new ReaderPlayback(Session);
        Playback.SetSpeed(settings.TimedWordsPerMinute);
        SpeedSlider.Value = settings.TimedWordsPerMinute;
        Playback.StateChanged += () =>
        {
            TimedPlay.Content = Playback.Playing ? "Pause" : "Play timed";
            PlaybackLabel.Text = Playback.Waiting ? "Waiting for the next paragraph · pause to hold here" : Playback.Playing ? "Timed scrolling · Space in reader to pause" : "Timed scrolling paused";
        };
        this.hiddenTestMode = hiddenTestMode;
        if (hiddenTestMode) { ShowActivated = false; ShowInTaskbar = false; Opacity = 0; }
        Session.Load(string.IsNullOrWhiteSpace(settings.ScriptText) ? ReaderSession.Sample : settings.ScriptText);
        Session.SetStyle(settings.ReaderStyle);
        ScriptEditor.Text = Session.Text;
        FontSlider.Value = settings.ReaderStyle.FontSize; WidthSlider.Value = settings.ReaderStyle.ColumnCharacters;
        SpacingSlider.Value = settings.ReaderStyle.LineSpacing; OpacitySlider.Value = settings.ReaderStyle.BackgroundOpacity * 100; BandSlider.Value = settings.ReaderStyle.ReadingBand * 100;
        RememberPosition.IsChecked = settings.RememberReaderPosition;
        VersionText.ToolTip = "Settings and diagnostics: " + dataDirectory;
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        var keys = new ApiKeyStore(dataDirectory);
        Voice = new VoiceService(Dispatcher, Session, keys, log);
        Recorder = new RecordingService(System.IO.Path.Combine(dataDirectory, "recordings"), log, hiddenTestMode ? AudioRecordingTests.CreateSource : null);
        Voice.Status += text => SetStatus(text); Voice.Heard += text => HeardText.Text = "Heard: " + text; Voice.Level += level => MicLevel.Value = level;
        Voice.Stopped += () => { MicrophonePicker.IsEnabled = RefreshMicrophonesButton.IsEnabled = true; ListenButton.Content = "Start listening"; MicLabel.Text = "Mic off"; MicLevel.Value = 0; };
        Session.Changed += change =>
        {
            if (change is ReaderChange.Position or ReaderChange.Document)
            { replay?.Stop(); Voice.Reanchor(); if (Voice.Running || startingVoice) _ = StopListening(); }
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += PlaybackFrame;
            if (settings.RememberReaderPosition) foreach (var saved in settings.Panels.ToArray()) AddReader(saved);
        };
        Closed += (_, _) => CompositionTarget.Rendering -= PlaybackFrame;
        initialized = true;
        RefreshMicrophones();
        if (!keys.Exists) SetStatus("Deepgram key is not configured yet. Ask Codex to finish setup.", true);
        UpdatePanelCount();
    }
    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) Dispatcher.BeginInvoke(() => { if (!closing) NativeWindows.Restore(this, NativeWindows.GetBounds(this)); });
        return IntPtr.Zero;
    }
    public void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(151, 63, 43) : Color.FromRgb(96, 113, 107));
    }
    internal ReaderWindow AddReader(PanelPlacement? placement = null)
    {
        var panel = new ReaderWindow(Session, Playback); panels.Add(panel);
        panel.PlaybackRequested += async () => await ToggleTimed();
        panel.SentenceRequested += direction => Playback.Sentence(direction);
        if (hiddenTestMode) { panel.ShowActivated = false; panel.ShowInTaskbar = false; panel.Opacity = 0; }
        panel.ResetRequested += ResetPosition; panel.LocationChanged += (_, _) => ScheduleSave(); panel.SizeChanged += (_, _) => ScheduleSave();
        panel.Closed += (_, _) => { panels.Remove(panel); UpdatePanelCount(); if (!closing) { ScheduleSave(); if (panels.Count == 0) { Playback.Pause(); _ = StopListening(); } } };
        panel.Show();
        if (placement is not null) NativeWindows.Restore(panel, placement);
        else if (panels.Count > 1)
        {
            var first = NativeWindows.GetBounds(panels[0]);
            NativeWindows.Restore(panel, first with { Left = first.Left + panels.Count * 28, Top = first.Top + panels.Count * 28 });
        }
        log.Write("Reader opened; count=" + panels.Count + "; captureExclusion=" + panel.CaptureExcluded);
        if (!panel.CaptureExcluded) SetStatus("Capture exclusion failed for a panel. Check before sharing your screen.", true);
        UpdatePanelCount(); ScheduleSave(); return panel;
    }
    private void UpdatePanelCount() { ReaderState.Text = panels.Count == 1 ? "1 panel" : $"{panels.Count} panels"; CloseReaderButton.IsEnabled = panels.Count > 0; }
    private void ScheduleSave() { if (!initialized || closing) return; saveTimer.Stop(); saveTimer.Start(); }
    private async void OpenReader_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Answer is null && ScriptEditor.Text.Trim() != Session.Text && !await ApplyText()) return;
        if (panels.Count == 0) AddReader(); else panels[0].Activate();
    }
    private void AddPanel_Click(object sender, RoutedEventArgs e) => AddReader();
    private void CloseReader_Click(object sender, RoutedEventArgs e) { foreach (var panel in panels.ToArray()) panel.Close(); }
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyText();
    private async void Sample_Click(object sender, RoutedEventArgs e) { ScriptEditor.Text = ReaderSession.Sample; await ApplyText(); }
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetPosition();
    private void ResetPosition() { Session.Select(0); Voice.Reanchor(); HeardText.Text = "Position reset. Read from the beginning."; }
    private void ScriptEditor_Changed(object sender, TextChangedEventArgs e) { if (initialized) SetStatus("Text edited. Apply it or start listening to use this script."); }
    internal async Task<bool> ApplyText()
    {
        if (string.IsNullOrWhiteSpace(ScriptEditor.Text)) { SetStatus("Enter a sentence first.", true); return false; }
        await StopListening(); StreamDemo?.Close(); Session.Load(ScriptEditor.Text); SaveSettings(); SetStatus("Script loaded. Open a reader or start listening."); return true;
    }
    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (startingVoice) return;
        if (Voice.Running) { await StopListening(); return; }
        replay?.Stop();
        Playback.UseVoice();
        if (Session.Answer is null && ScriptEditor.Text.Trim() != Session.Text && !await ApplyText()) return;
        if (panels.Count == 0) AddReader();
        if (Session.Answer is null && Session.Position >= Session.Words.Count) ResetPosition();
        if (MicrophonePicker.SelectedItem is not Microphone microphone) { SetStatus("Select an available microphone, then start listening.", true); return; }
        startingVoice = true; ListenButton.IsEnabled = false;
        MicrophonePicker.IsEnabled = RefreshMicrophonesButton.IsEnabled = false;
        await Voice.StartAsync(microphone.Id); startingVoice = false; ListenButton.IsEnabled = true;
        MicrophonePicker.IsEnabled = RefreshMicrophonesButton.IsEnabled = !Voice.Running;
        ListenButton.Content = Voice.Running ? "Stop listening" : "Start listening"; MicLabel.Text = Voice.Running ? "Mic listening" : "Mic off";
    }
    internal async Task StopListening()
    {
        replay?.Stop();
        await Voice.StopAsync(); ListenButton.Content = "Start listening"; MicLabel.Text = "Mic off";
        MicrophonePicker.IsEnabled = RefreshMicrophonesButton.IsEnabled = true;
        if (!closing) SetStatus("Microphone off. Your reading position is preserved.");
    }
    private void PlaybackFrame(object? sender, EventArgs e)
    {
        var now = playbackClock.Elapsed.TotalSeconds;
        Playback.Tick(now - playbackFrame);
        var replayWasRunning = replay?.Running == true;
        replay?.Tick(now - playbackFrame);
        if (replayWasRunning && replay?.Running == false) SetStatus("Dummy speech replay complete · microphone off. Reset or resume when ready.");
        playbackFrame = now;
    }
    private async void TimedPlay_Click(object sender, RoutedEventArgs e) => await ToggleTimed();
    internal async Task ToggleTimed()
    {
        if (Playback.Playing) { Playback.Pause(); return; }
        replay?.Stop();
        // Cancel even an in-flight voice startup before allowing the timed clock to own progress.
        await StopListening();
        if (closing) return;
        if (Session.Answer is null && ScriptEditor.Text.Trim() != Session.Text && !await ApplyText()) return;
        if (panels.Count == 0) AddReader();
        Playback.Play(); playbackFrame = playbackClock.Elapsed.TotalSeconds;
        SetStatus("Timed mode · microphone off. Click a word or choose a sentence to pause and reposition.");
    }
    private void PreviousSentence_Click(object sender, RoutedEventArgs e) => Playback.Sentence(-1);
    private void NextSentence_Click(object sender, RoutedEventArgs e) => Playback.Sentence(1);
    private void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!initialized) return; Playback.SetSpeed(SpeedSlider.Value); ScheduleSave(); }
    private async void ReplaySpeech_Click(object sender, RoutedEventArgs e)
    {
        StreamDemo?.Close(); replay?.Stop(); Playback.UseVoice(); await StopListening();
        if (closing) return;
        if (Session.Answer is null && ScriptEditor.Text.Trim() != Session.Text && !await ApplyText()) return;
        if (Session.Words.Count < 15) { SetStatus("Use at least 15 words for the dummy-speech replay.", true); return; }
        Session.Select(0);
        if (panels.Count == 0) AddReader();
        replay = new ScriptedSpeechSource(Session);
        replay.Heard += (text, label) => { HeardText.Text = "Demo heard: " + text; SetStatus("Dummy speech · " + label + " · no microphone or network"); };
        playbackFrame = playbackClock.Elapsed.TotalSeconds;
        SetStatus("Dummy speech replay: pauses, delays, repeats and skipped words. Manual navigation stops the replay.");
    }
    private void ReadingStudy_Click(object sender, RoutedEventArgs e)
    {
        StreamDemo?.Close();
        if (study is not null) { study.Activate(); return; }
        try
        {
            study = new ReadingStudyWindow(this, System.IO.Path.Combine(dataDirectory, "reading-study"));
            study.Closed += (_, _) => study = null;
            study.Show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { SetStatus("Reading comparison could not open. Existing results are preserved; check the data folder.", true); }
    }
    private void Recording_Click(object sender, RoutedEventArgs e) => OpenRecording();
    internal RecordingWindow OpenRecording()
    {
        if (recordingWindow is not null) { if (!hiddenTestMode) recordingWindow.Activate(); return recordingWindow; }
        recordingWindow = new RecordingWindow(this, System.IO.Path.Combine(dataDirectory, "recordings"), hiddenTestMode);
        recordingWindow.Closed += (_, _) => recordingWindow = null;
        recordingWindow.Show(); return recordingWindow;
    }
    internal void SaveRecordingDevices(string microphone, string output)
    {
        settings = settings with { RecordingMicrophoneId = microphone, RecordingOutputId = output }; SaveSettings();
    }
    private void StreamDemo_Click(object sender, RoutedEventArgs e) => OpenStreamDemo();
    internal StreamDemoWindow OpenStreamDemo()
    {
        if (StreamDemo is not null) { if (!hiddenTestMode) StreamDemo.Activate(); return StreamDemo; }
        study?.Close();
        Playback.Pause(); _ = StopListening();
        StreamDemo = new StreamDemoWindow(this, hiddenTestMode);
        StreamDemo.Closed += (_, _) => StreamDemo = null;
        StreamDemo.Show(); return StreamDemo;
    }
    private void Appearance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized) return;
        Session.SetStyle(new(FontSlider.Value, WidthSlider.Value, SpacingSlider.Value, OpacitySlider.Value / 100, BandSlider.Value / 100)); ScheduleSave();
    }
    private void RememberPosition_Changed(object sender, RoutedEventArgs e) { if (initialized) ScheduleSave(); }
    private void RefreshMicrophones_Click(object sender, RoutedEventArgs e) => RefreshMicrophones();
    private void RefreshMicrophones()
    {
        var selected = (MicrophonePicker.SelectedItem as Microphone)?.Id ?? settings.MicrophoneId;
        try
        {
            var inputs = VoiceService.Microphones();
            MicrophonePicker.ItemsSource = inputs;
            MicrophonePicker.SelectedItem = inputs.FirstOrDefault(m => m.Id == selected) ?? (selected is null ? inputs.FirstOrDefault() : null);
            if (inputs.Count == 0) SetStatus("No microphones found. Connect a microphone and press Refresh.", true);
            else if (MicrophonePicker.SelectedItem is null) SetStatus("Your saved microphone is disconnected. Select an available input.", true);
        }
        catch (Exception ex) { log.Write("Microphone enumeration failure type=" + ex.GetType().Name); SetStatus("Microphones could not be listed. Check Windows audio settings and press Refresh.", true); }
    }
    private void Microphone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && MicrophonePicker.SelectedItem is Microphone microphone)
        { settings = settings with { MicrophoneId = microphone.Id }; ScheduleSave(); }
    }
    private void SaveSettings()
    {
        try
        {
            settings = settings with { RememberReaderPosition = RememberPosition.IsChecked == true, ScriptText = Session.Answer is null ? Session.Text : settings.ScriptText, ReaderStyle = Session.Style, TimedWordsPerMinute = Playback.WordsPerMinute,
                Panels = RememberPosition.IsChecked == true ? panels.Select(NativeWindows.GetBounds).ToList() : [] };
            store.Save(settings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { log.Write("Settings save failed: " + ex.Message); SetStatus("Settings could not be saved. Check folder permissions.", true); }
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (allowClose) { base.OnClosing(e); return; }
        e.Cancel = true; if (closing) return;
        closing = true; replay?.Stop(); StreamDemo?.Close(); study?.Close(); Playback.Pause(); saveTimer.Stop(); SaveSettings(); await Voice.StopAsync(); await Recorder.StopAsync();
        if (recordingWindow is { } captureWindow) { try { await captureWindow.RecoveryTask; } catch (Exception) { } if (captureWindow.IsVisible) captureWindow.Close(); }
        foreach (var panel in panels.ToArray()) panel.Close();
        allowClose = true;
        await Dispatcher.InvokeAsync(Close);
    }
}
