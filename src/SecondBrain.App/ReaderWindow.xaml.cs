using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ReaderPlayback playback;
    private readonly List<(int Word, double Offset)> lines = [];
    private int paintedPosition = -1;
    private bool paintedTimed;
    private readonly List<Run> runs = [];
    private readonly List<TextBlock> wordOwners = [];
    private int displayedBlocks;
    private int measuredWords;
    private bool appendDirty;
    private bool alignTargetPending;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ReaderMotion motion = new();
    private double lastFrame;
    private bool layoutDirty = true, snapPending, alignmentQueued, closed;
    private static readonly Brush ReadBrush = FrozenBrush(125, 151, 138);
    private static readonly Brush CurrentBrush = FrozenBrush(207, 237, 153);
    private static readonly Brush UnreadBrush = FrozenBrush(237, 243, 233);
    internal bool CaptureExcluded { get; private set; }
    internal int SelectedWord => session.Position;
    internal double ScrollPosition => motion.Position;
    internal double VisualTarget => motion.Target;
    internal bool IsGliding => motion.Moving;
    internal double LineHeight => session.Style.FontSize * session.Style.LineSpacing;
    internal double WordLine(int index) => runs[index].ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
    internal double WordScreenY(int index) => WordTop(index);
    internal int DisplayedBlockCount => displayedBlocks;
    internal double AnchorError => runs.Count == 0 ? 0 : Math.Abs(WordTop(Math.Min(session.Position, runs.Count - 1)) - ReadingArea.ActualHeight * session.Style.ReadingBand);
    public event Action? ResetRequested;
    public event Action? PlaybackRequested;
    public event Action<int>? SentenceRequested;

    public ReaderWindow(ReaderSession session, ReaderPlayback playback)
    {
        this.session = session;
        this.playback = playback;
        InitializeComponent();
        playback.StateChanged += PlaybackChanged;
        PlaybackChanged();
        session.Changed += SessionChanged;
        SourceInitialized += (_, _) =>
        {
            CaptureExcluded = NativeWindows.ExcludeFromCapture(this);
            CaptureText.Text = CaptureExcluded ? "Capture exclusion on · verify your screen share" : "Capture exclusion failed · panel may appear in screen share";
            if (!CaptureExcluded) CaptureText.Foreground = Brushes.Salmon;
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
        };
        Loaded += (_, _) => { Rebuild(); CompositionTarget.Rendering += RenderFrame; };
        SizeChanged += (_, _) => { layoutDirty = true; QueueAlignment(false); };
        Closed += (_, _) => { closed = true; session.Changed -= SessionChanged; playback.StateChanged -= PlaybackChanged; CompositionTarget.Rendering -= RenderFrame; };
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) Dispatcher.BeginInvoke(() => { if (!closed) NativeWindows.Restore(this, NativeWindows.GetBounds(this)); });
        return IntPtr.Zero;
    }
    private void SessionChanged(ReaderChange change)
    {
        if (change == ReaderChange.StreamState) { PaintCounter(); return; }
        if (change == ReaderChange.Append)
        {
            AppendBlocks(); appendDirty = true; PaintProgress(); QueueAlignment(true, preserveOffset: true); return;
        }
        if (change == ReaderChange.TimedPosition) { PaintCounter(); return; }
        if (change == ReaderChange.Document) Rebuild();
        else
        {
            if (change == ReaderChange.Appearance) layoutDirty = true;
            PaintProgress(); QueueAlignment(change == ReaderChange.VoicePosition);
        }
    }
    private void Rebuild()
    {
        layoutDirty = true;
        paintedPosition = -1;
        runs.Clear(); wordOwners.Clear(); ScriptText.Inlines.Clear(); StreamBlocks.Children.Clear(); displayedBlocks = 0;
        ScriptText.Visibility = session.Answer is null ? Visibility.Visible : Visibility.Collapsed;
        if (session.Answer is not null) AppendBlocks();
        else foreach (var word in session.Words)
        {
            var run = new Run(word.Text);
            run.MouseLeftButtonDown += (_, e) => { session.Select(word.Id); e.Handled = true; };
            ScriptText.Inlines.Add(run); ScriptText.Inlines.Add(new Run(word.Suffix)); runs.Add(run); wordOwners.Add(ScriptText);
        }
        PaintProgress(); QueueAlignment(false);
    }
    private void AppendBlocks()
    {
        if (session.Answer is not { } answer) return;
        foreach (var block in answer.Blocks.Skip(displayedBlocks))
        {
            var text = new TextBlock { Foreground = UnreadBrush, FontFamily = ScriptText.FontFamily,
                TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                FontSize = session.Style.FontSize, LineHeight = LineHeight, Width = ScriptText.Width,
                Margin = new Thickness(0, 0, 0, LineHeight) };
            foreach (var word in block.Words)
            {
                // Keep whitespace in the same inline to halve the streaming layout objects.
                var run = new Run(word.Text + (word == block.Words[^1] ? "" : word.Suffix))
                { Foreground = playback.TimedMode ? UnreadBrush : word.Id < session.Position ? ReadBrush : word.Id == session.Position ? CurrentBrush : UnreadBrush };
                run.MouseLeftButtonDown += (_, e) => { session.Select(word.Id); e.Handled = true; };
                text.Inlines.Add(run);
                // Block spacing belongs to the container; appending never edits an older inline.
                runs.Add(run); wordOwners.Add(text);
            }
            StreamBlocks.Children.Add(text); displayedBlocks++;
        }
    }
    private void PaintProgress()
    {
        if (playback.TimedMode)
        {
            if (!paintedTimed || paintedPosition < 0) foreach (var run in runs) run.Foreground = UnreadBrush;
            paintedTimed = true; paintedPosition = session.Position; PaintCounter(); return;
        }
        if (paintedTimed) paintedPosition = -1;
        paintedTimed = false;
        var first = paintedPosition < 0 ? 0 : Math.Min(paintedPosition, session.Position);
        var last = paintedPosition < 0 ? runs.Count - 1 : Math.Min(runs.Count - 1, Math.Max(paintedPosition, session.Position));
        for (var i = first; i <= last; i++)
            runs[i].Foreground = i < session.Position ? ReadBrush : i == session.Position ? CurrentBrush : UnreadBrush;
        paintedPosition = session.Position;
        PaintCounter();
    }
    private void PaintCounter() => ProgressText.Text = session.Position >= runs.Count
        ? session.AwaitingText ? "Waiting for more text…" : session.Answer is { } answer ? answer.Detail : "Complete · reset to read again"
        : $"Word {session.Position + 1} / {runs.Count}";
    private void QueueAlignment(bool smooth, bool preserveOffset = false)
    {
        // Manual/layout requests win if a voice update arrives in the same dispatch turn.
        snapPending |= !smooth;
        alignTargetPending |= !preserveOffset;
        if (alignmentQueued || !IsLoaded || closed) return;
        alignmentQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            alignmentQueued = false;
            if (closed) return;
            var style = session.Style;
            var band = ReadingArea.ActualHeight * style.ReadingBand;
            if (layoutDirty || appendDirty)
            {
                if (layoutDirty)
                {
                    Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(style.BackgroundOpacity * 255), 23, 39, 35));
                    ScriptText.FontSize = style.FontSize; ScriptText.LineHeight = style.FontSize * style.LineSpacing;
                    ReaderContent.Width = Math.Max(120, ReadingArea.ActualWidth - 64);
                    ScriptText.Width = Math.Max(120, Math.Min(ReaderContent.Width, style.ColumnCharacters * style.FontSize * .5));
                    foreach (TextBlock block in StreamBlocks.Children)
                    {
                        block.FontSize = style.FontSize; block.LineHeight = style.FontSize * style.LineSpacing;
                        block.Width = ScriptText.Width; block.Margin = new Thickness(0, 0, 0, block.LineHeight);
                    }
                    TopSpace.Height = band;
                    System.Windows.Controls.Canvas.SetTop(Band, band + style.FontSize * .55);
                    }
                UpdateLayout();
                if (layoutDirty) { lines.Clear(); measuredWords = 0; }
                else if (lines.Count > 0) lines.RemoveAt(lines.Count - 1);
                for (var i = measuredWords; i < runs.Count; i++)
                {
                    var offset = motion.Position + WordTop(i) - band;
                    if (lines.Count == 0 || offset > lines[^1].Offset + 1) lines.Add((i, offset));
                }
                if (lines.Count > 0) lines.Add((runs.Count, lines[^1].Offset));
                measuredWords = runs.Count; layoutDirty = false; appendDirty = false;
            }
            if (runs.Count == 0 || !alignTargetPending) { alignTargetPending = false; snapPending = false; return; }
            var target = Math.Max(0, motion.Position + WordTop(Math.Min(session.Position, runs.Count - 1)) - band);
            if (snapPending)
            {
                motion.Reset(target); TextTranslation.Y = -motion.Position;
                lastFrame = clock.Elapsed.TotalSeconds;
            }
            else motion.Follow(target);
            snapPending = false;
            alignTargetPending = false;
        }, DispatcherPriority.Loaded);
    }
    private double WordTop(int index)
    {
        var rect = runs[index].ContentStart.GetCharacterRect(LogicalDirection.Forward);
        return wordOwners[index].TranslatePoint(new Point(rect.Left, rect.Top), ReadingArea).Y;
    }
    private void RenderFrame(object? sender, EventArgs args)
    {
        var now = clock.Elapsed.TotalSeconds; var dt = now - lastFrame; lastFrame = now;
        if (playback.TimedMode)
        {
            if (!playback.Playing || alignmentQueued) return;
            motion.Follow(FlowOffset(playback.Cursor));
        }
        if (!motion.Moving || alignmentQueued) return;
        TextTranslation.Y = -motion.Step(dt, LineHeight);
    }
    private double FlowOffset(double cursor)
    {
        if (lines.Count < 2) return motion.Position;
        var left = 0; var right = lines.Count - 1;
        while (right - left > 1)
        { var middle = (left + right) / 2; if (lines[middle].Word <= cursor) left = middle; else right = middle; }
        var start = lines[left]; var end = lines[right];
        return start.Offset + (end.Offset - start.Offset) * Math.Clamp((cursor - start.Word) / Math.Max(1, end.Word - start.Word), 0, 1);
    }
    private void PlaybackChanged()
    {
        ReaderPlay.Content = playback.Playing ? "Pause" : "Play timed";
        if (paintedTimed != playback.TimedMode) PaintProgress();
        if (!playback.Playing) motion.Reset(motion.Position);
    }
    private void Play_Click(object sender, RoutedEventArgs e) => PlaybackRequested?.Invoke();
    private void Previous_Click(object sender, RoutedEventArgs e) => SentenceRequested?.Invoke(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => SentenceRequested?.Invoke(1);
    private static Brush FrozenBrush(byte red, byte green, byte blue)
    { var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush; }
    private void Title_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    { Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange); Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange); }
    private void Reader_MouseWheel(object sender, MouseWheelEventArgs e)
    { session.Select(session.Position + (e.Delta < 0 ? 3 : -3)); e.Handled = true; }
    private void Reader_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { PlaybackRequested?.Invoke(); e.Handled = true; return; }
        if (e.Key is Key.Left or Key.Right) { SentenceRequested?.Invoke(e.Key == Key.Left ? -1 : 1); e.Handled = true; return; }
        var next = e.Key switch { Key.Down => session.Position + 1, Key.Up => session.Position - 1, Key.PageDown => session.Position + 10, Key.PageUp => session.Position - 10, Key.Home => 0, Key.End => session.Words.Count - 1, _ => -1 };
        if (next >= 0) { session.Select(next); e.Handled = true; }
    }
}
