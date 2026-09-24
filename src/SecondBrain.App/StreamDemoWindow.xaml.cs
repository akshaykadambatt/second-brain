using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class StreamDemoWindow : Window
{
    private readonly MainWindow main;
    private readonly string originalScript;
    private readonly int originalPosition;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private double lastTick;
    private readonly List<ScriptedAnswerSource> sources = [];
    private readonly Dictionary<Guid, ListBoxItem> rows = [];
    private bool closed, starting;
    private int run;
    private int navigation;
    internal AnswerInbox Inbox { get; } = new();
    internal StreamAnswer? Active => main.Session.Answer;
    public StreamDemoWindow(MainWindow main, bool hidden = false)
    {
        this.main = main; Owner = main; originalScript = main.Session.Text; originalPosition = main.Session.Position;
        InitializeComponent();
        if (hidden) { Opacity = 0; ShowActivated = false; ShowInTaskbar = false; }
        Inbox.Changed += Updated;
        timer.Tick += (_, _) =>
        {
            var now = clock.Elapsed.TotalSeconds;
            foreach (var source in sources) source.Tick(now - lastTick);
            lastTick = now;
        };
        timer.Start();
        Closed += (_, _) =>
        {
            closed = true; timer.Stop(); foreach (var source in sources) source.Stop(); Inbox.Changed -= Updated;
            main.Playback.Pause(); main.Session.Load(originalScript); main.Session.Select(originalPosition);
        };
    }
    private void Updated(StreamAnswer answer)
    {
        if (!rows.TryGetValue(answer.Id, out var row))
        {
            row = new ListBoxItem { Tag = answer, Padding = new Thickness(8), Content = new TextBlock { TextWrapping = TextWrapping.Wrap } };
            rows.Add(answer.Id, row); AnswersList.Items.Add(row);
        }
        ((TextBlock)row.Content).Text = $"{answer.Title} · {answer.WordCount} words\n{answer.Detail}";
        main.Session.RefreshAnswer(answer); UpdateActive();
    }
    private void UpdateActive()
    {
        ActiveText.Text = Active is { } answer ? $"Reading: {answer.Title} · {answer.Detail}" : "Your script is still in the reader.";
    }
    internal async Task StartDemo()
    {
        if (starting || closed) return;
        starting = true;
        try
        {
            if (Inbox.Answers.Count > AnswerInbox.Capacity - 3) { Status.Text = "Demo history is full. Close and reopen this window to clear it."; return; }
            await main.StopListening(); if (closed) return;
            var source = new ScriptedAnswerSource(Inbox, ++run); sources.Add(source);
            if (Active is null) await SelectAnswer(source.First);
            RunButton.Content = "Queue another demo";
            Status.Text = "Text arrives over 14 seconds, with a long pause, reordered chunks, a duplicate, and an interrupted answer. Choose Play timed to try end-of-text waiting.";
        }
        finally { starting = false; }
    }
    internal async Task SelectAnswer(StreamAnswer answer)
    {
        var request = ++navigation;
        await main.StopListening(); if (closed || request != navigation) return;
        if (Active is { } prior) prior.SavedPosition = main.Session.Position;
        main.Session.ShowAnswer(answer);
        if (main.Panels.Count == 0) main.AddReader();
        AnswersList.SelectedItem = rows[answer.Id]; UpdateActive();
    }
    private async void Run_Click(object sender, RoutedEventArgs e) => await StartDemo();
    private async void Read_Click(object sender, RoutedEventArgs e)
    { if (AnswersList.SelectedItem is ListBoxItem { Tag: StreamAnswer answer }) await SelectAnswer(answer); }
    private async Task Navigate(int direction)
    {
        var answers = Inbox.Answers; if (answers.Count == 0) return;
        var index = answers.ToList().FindIndex(a => a == Active);
        await SelectAnswer(answers[Math.Clamp(index + direction, 0, answers.Count - 1)]);
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Navigate(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await Navigate(1);
    private void Stop_Click(object sender, RoutedEventArgs e)
    { foreach (var source in sources) source.Stop(); Status.Text = "Incoming demo events stopped. Completed paragraphs remain available."; }
    private void Return_Click(object sender, RoutedEventArgs e) => Close();
}
