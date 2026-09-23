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
    internal ReaderSession Session { get; } = new();
    internal VoiceService Voice { get; }
    internal IReadOnlyList<ReaderWindow> Panels => panels;
    internal AppSettings Settings => settings;

    public MainWindow(SettingsStore store, AppSettings settings, DiagnosticLog log, string dataDirectory, bool hiddenTestMode = false)
    {
        this.store = store; this.settings = settings; this.log = log;
        InitializeComponent();
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
        Voice = new VoiceService(Dispatcher, Session);
        Voice.Status += text => SetStatus(text); Voice.Heard += text => HeardText.Text = "Heard: " + text; Voice.Level += level => MicLevel.Value = level;
        Voice.Stopped += () => { ListenButton.Content = "Start listening"; MicLabel.Text = "Mic off"; MicLevel.Value = 0; };
        Session.Changed += change => { if (change is ReaderChange.Position or ReaderChange.Document) Voice.Reanchor(); };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
        Loaded += (_, _) => { if (settings.RememberReaderPosition) foreach (var saved in settings.Panels.ToArray()) AddReader(saved); };
        initialized = true;
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
        var panel = new ReaderWindow(Session); panels.Add(panel);
        if (hiddenTestMode) { panel.ShowActivated = false; panel.ShowInTaskbar = false; panel.Opacity = 0; }
        panel.ResetRequested += ResetPosition; panel.LocationChanged += (_, _) => ScheduleSave(); panel.SizeChanged += (_, _) => ScheduleSave();
        panel.Closed += (_, _) => { panels.Remove(panel); UpdatePanelCount(); if (!closing) { ScheduleSave(); if (panels.Count == 0) _ = StopListening(); } };
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
        if (ScriptEditor.Text.Trim() != Session.Text && !await ApplyText()) return;
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
        await StopListening(); Session.Load(ScriptEditor.Text); SaveSettings(); SetStatus("Script loaded. Open a reader or start listening."); return true;
    }
    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (startingVoice) return;
        if (Voice.Running) { await StopListening(); return; }
        if (ScriptEditor.Text.Trim() != Session.Text && !await ApplyText()) return;
        if (panels.Count == 0) AddReader();
        if (Session.Position >= Session.Words.Count) ResetPosition();
        startingVoice = true; ListenButton.IsEnabled = false;
        await Voice.StartAsync(); startingVoice = false; ListenButton.IsEnabled = true;
        ListenButton.Content = Voice.Running ? "Stop listening" : "Start listening"; MicLabel.Text = Voice.Running ? "Mic listening" : "Mic off";
    }
    internal async Task StopListening()
    {
        await Voice.StopAsync(); ListenButton.Content = "Start listening"; MicLabel.Text = "Mic off";
        if (!closing) SetStatus("Microphone off. Your reading position is preserved.");
    }
    private void Appearance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized) return;
        Session.SetStyle(new(FontSlider.Value, WidthSlider.Value, SpacingSlider.Value, OpacitySlider.Value / 100, BandSlider.Value / 100)); ScheduleSave();
    }
    private void RememberPosition_Changed(object sender, RoutedEventArgs e) { if (initialized) ScheduleSave(); }
    private void SaveSettings()
    {
        try
        {
            settings = settings with { RememberReaderPosition = RememberPosition.IsChecked == true, ScriptText = Session.Text, ReaderStyle = Session.Style,
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
        closing = true; saveTimer.Stop(); SaveSettings(); await Voice.StopAsync();
        foreach (var panel in panels.ToArray()) panel.Close();
        allowClose = true;
        await Dispatcher.InvokeAsync(Close);
    }
}
