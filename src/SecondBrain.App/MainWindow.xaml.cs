using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly DiagnosticLog log;
    private AppSettings settings;
    private bool initialized;
    internal ReaderWindow? Reader { get; private set; }
    internal AppSettings Settings => settings;

    public MainWindow(SettingsStore store, AppSettings settings, DiagnosticLog log, string dataDirectory)
    {
        this.store = store;
        this.settings = settings;
        this.log = log;
        InitializeComponent();
        RememberPosition.IsChecked = settings.RememberReaderPosition;
        VersionText.ToolTip = "Settings and diagnostic logs: " + dataDirectory;
        initialized = true;
    }

    public void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(151, 63, 43) : Color.FromRgb(96, 113, 107));
    }

    internal void OpenReader()
    {
        if (Reader is not null) { Reader.Activate(); return; }
        Reader = new ReaderWindow();
        if (settings.RememberReaderPosition && settings.ReaderPlacement is { } p)
        {
            // Sprint 0 safely falls back to the primary work area for off-screen positions.
            // Per-monitor layout recovery is a Sprint 1 requirement.
            var area = SystemParameters.WorkArea;
            Reader.Width = Math.Min(p.Width, area.Width);
            Reader.Height = Math.Min(p.Height, area.Height);
            Reader.Left = Math.Clamp(p.Left, area.Left, Math.Max(area.Left, area.Right - Reader.Width));
            Reader.Top = Math.Clamp(p.Top, area.Top, Math.Max(area.Top, area.Bottom - Reader.Height));
            Reader.WindowStartupLocation = WindowStartupLocation.Manual;
        }
        var savedOnClose = true;
        Reader.Closing += (_, _) =>
        {
            if (settings.RememberReaderPosition)
            {
                var bounds = Reader.RestoreBounds;
                settings = settings with { ReaderPlacement = new(bounds.Left, bounds.Top, bounds.Width, bounds.Height) };
            }
            savedOnClose = SaveSettings();
        };
        Reader.Closed += (_, _) =>
        {
            Reader = null;
            CloseReaderButton.IsEnabled = false;
            ReaderState.Text = "Closed";
            OpenReaderButton.Content = "Open reader";
            if (savedOnClose) SetStatus("Reader closed. Your workspace is ready.");
            log.Write("Reader closed");
        };
        Reader.Show();
        CloseReaderButton.IsEnabled = true;
        OpenReaderButton.Content = "Focus reader";
        ReaderState.Text = "Open";
        log.Write("Reader opened");
        SetStatus("Reader open. Close it here or with its window controls.");
    }

    private void OpenReader_Click(object sender, RoutedEventArgs e) => OpenReader();
    private void CloseReader_Click(object sender, RoutedEventArgs e) => Reader?.Close();
    private void RememberPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings = settings with { RememberReaderPosition = RememberPosition.IsChecked == true };
        SaveSettings();
    }

    private bool SaveSettings()
    {
        try { store.Save(settings); log.Write("Settings saved"); return true; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            log.Write("Settings save failed: " + ex.Message);
            SetStatus("Settings could not be saved. Check the data folder permissions.", true);
            return false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Reader?.Close();
        SaveSettings();
        base.OnClosing(e);
    }
}
