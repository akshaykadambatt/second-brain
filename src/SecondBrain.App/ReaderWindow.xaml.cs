using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class ReaderWindow : Window
{
    private readonly ReaderSession session;
    private readonly List<Run> runs = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastFrame, targetOffset;
    private bool animate, alignmentQueued, closed;
    internal bool CaptureExcluded { get; private set; }
    internal int SelectedWord => session.Position;
    internal double AnchorError => runs.Count == 0 ? 0 : Math.Abs(WordTop(Math.Min(session.Position, runs.Count - 1)) - ReadingArea.ActualHeight * session.Style.ReadingBand);
    public event Action? ResetRequested;

    public ReaderWindow(ReaderSession session)
    {
        this.session = session;
        InitializeComponent();
        session.Changed += SessionChanged;
        SourceInitialized += (_, _) =>
        {
            CaptureExcluded = NativeWindows.ExcludeFromCapture(this);
            CaptureText.Text = CaptureExcluded ? "Capture exclusion on · verify your screen share" : "Capture exclusion failed · panel may appear in screen share";
            if (!CaptureExcluded) CaptureText.Foreground = Brushes.Salmon;
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
        };
        Loaded += (_, _) => { Rebuild(); CompositionTarget.Rendering += RenderFrame; };
        SizeChanged += (_, _) => QueueAlignment(false);
        Closed += (_, _) => { closed = true; session.Changed -= SessionChanged; CompositionTarget.Rendering -= RenderFrame; };
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) Dispatcher.BeginInvoke(() => { if (!closed) NativeWindows.Restore(this, NativeWindows.GetBounds(this)); });
        return IntPtr.Zero;
    }
    private void SessionChanged(ReaderChange change)
    {
        if (change == ReaderChange.Document) Rebuild();
        else { PaintProgress(); QueueAlignment(change == ReaderChange.VoicePosition); }
    }
    private void Rebuild()
    {
        runs.Clear(); ScriptText.Inlines.Clear();
        foreach (var word in session.Words)
        {
            var run = new Run(word.Text);
            run.MouseLeftButtonDown += (_, e) => { session.Select(word.Id); e.Handled = true; };
            ScriptText.Inlines.Add(run); ScriptText.Inlines.Add(new Run(word.Suffix)); runs.Add(run);
        }
        PaintProgress(); QueueAlignment(false);
    }
    private void PaintProgress()
    {
        for (var i = 0; i < runs.Count; i++)
            runs[i].Foreground = i < session.Position ? new SolidColorBrush(Color.FromRgb(125, 151, 138))
                : i == session.Position ? new SolidColorBrush(Color.FromRgb(207, 237, 153)) : new SolidColorBrush(Color.FromRgb(237, 243, 233));
        ProgressText.Text = session.Position >= runs.Count ? "Complete · reset to read again" : $"Word {session.Position + 1} / {runs.Count}";
    }
    private void QueueAlignment(bool smooth)
    {
        animate = smooth;
        if (alignmentQueued || !IsLoaded || closed) return;
        alignmentQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            alignmentQueued = false;
            if (closed || runs.Count == 0) return;
            var style = session.Style;
            Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(style.BackgroundOpacity * 255), 23, 39, 35));
            ScriptText.FontSize = style.FontSize; ScriptText.LineHeight = style.FontSize * style.LineSpacing;
            ScriptText.Width = Math.Max(120, Math.Min(Math.Max(120, ReadingArea.ActualWidth - 64), style.ColumnCharacters * style.FontSize * .5));
            var band = ReadingArea.ActualHeight * style.ReadingBand;
            TopSpace.Height = band; BottomSpace.Height = Math.Max(ReadingArea.ActualHeight, 100);
            System.Windows.Controls.Canvas.SetTop(Band, band + style.FontSize * .55);
            UpdateLayout();
            targetOffset = Math.Clamp(Viewport.VerticalOffset + WordTop(Math.Min(session.Position, runs.Count - 1)) - band, 0, Viewport.ScrollableHeight);
            if (!animate) Viewport.ScrollToVerticalOffset(targetOffset);
            lastFrame = clock.Elapsed.TotalSeconds;
        }, DispatcherPriority.Loaded);
    }
    private double WordTop(int index)
    {
        var rect = runs[index].ContentStart.GetCharacterRect(LogicalDirection.Forward);
        return ScriptText.TranslatePoint(new Point(rect.Left, rect.Top), ReadingArea).Y;
    }
    private void RenderFrame(object? sender, EventArgs args)
    {
        var now = clock.Elapsed.TotalSeconds; var dt = Math.Clamp(now - lastFrame, 0, .05); lastFrame = now;
        if (!animate) return;
        var remaining = targetOffset - Viewport.VerticalOffset;
        if (Math.Abs(remaining) < .5) { Viewport.ScrollToVerticalOffset(targetOffset); animate = false; return; }
        var step = Math.Clamp(remaining * (1 - Math.Exp(-dt / .12)), -650 * dt, 650 * dt);
        Viewport.ScrollToVerticalOffset(Viewport.VerticalOffset + step);
    }
    private void Title_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    { Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange); Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange); }
    private void Reader_MouseWheel(object sender, MouseWheelEventArgs e)
    { session.Select(session.Position + (e.Delta < 0 ? 3 : -3)); e.Handled = true; }
    private void Reader_KeyDown(object sender, KeyEventArgs e)
    {
        var next = e.Key switch { Key.Down => session.Position + 1, Key.Up => session.Position - 1, Key.PageDown => session.Position + 10, Key.PageUp => session.Position - 10, Key.Home => 0, Key.End => session.Words.Count - 1, _ => -1 };
        if (next >= 0) { session.Select(next); e.Handled = true; }
    }
}
