using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class AssistantWindow : Window
{
    private readonly MainWindow main;
    private readonly AssistantContext context;
    private readonly AssistantSettings settings;
    private readonly ApiKeyStore keys;
    private readonly IDisposable? ownedProvider;
    private readonly string originalScript;
    private readonly int originalPosition;
    private readonly Dictionary<Guid, ListBoxItem> rows = [];
    private readonly Queue<string> pending = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool closed, ready;
    private bool allowGeneralGuidance;
    private bool quietSuggestions;
    private int navigation;
    internal AssistantService Service { get; }
    internal Task ShutdownTask { get; private set; } = Task.CompletedTask;
    internal AssistantWindow(MainWindow main, AssistantContext context, string directory, DiagnosticLog log, bool hidden, IAnswerProvider? provider = null)
    {
        this.main = main; this.context = context; settings = new(directory); keys = new(directory, "OpenAI");
        Owner = main; originalScript = main.Session.Text; originalPosition = main.Session.Position;
        InitializeComponent();
        if (hidden) { Opacity = 0; ShowActivated = false; ShowInTaskbar = false; }
        if (provider is null) { var real = new OpenAiAnswerProvider(keys.Load); provider = real; ownedProvider = real; }
        Service = new(Dispatcher, provider, log);
        FastEffort.ItemsSource = DeepEffort.ItemsSource = new[] { "none", "low", "medium", "high" };
        AssistantOptions options = new();
        try { options = settings.Load(); } catch (Exception) { Status.Text = "Saved AI settings could not be read. Defaults loaded; save to repair."; }
        FastModel.Text = options.FastModel; DeepModel.Text = options.DeepModel; FastEffort.SelectedItem = options.FastEffort; DeepEffort.SelectedItem = options.DeepEffort;
        Deeper.IsChecked = options.Deeper; ContextNotes.Text = options.Context;
        allowGeneralGuidance = options.AllowGeneralGuidance;
        quietSuggestions = options.QuietSuggestions;
        Service.Inbox.Changed += Updated; Service.Changed += ServiceChanged;
        context.Received += Received; context.SessionChanged += SessionChanged;
        timer.Tick += (_, _) =>
        {
            Refresh();
            if (Automatic.IsChecked == true && Service.ActiveCount == 0 && pending.Count > 0 && !main.Voice.Running && !main.Playback.Voice.Active)
                Ask(pending.Dequeue(), true);
        };
        ready = true; timer.Start(); Refresh();
        Closed += (_, _) =>
        {
            closed = true; timer.Stop(); pending.Clear(); context.Received -= Received; context.SessionChanged -= SessionChanged;
            Service.Inbox.Changed -= Updated; Service.Changed -= ServiceChanged;
            try { settings.Save(Options()); } catch (Exception) { log.Write("AI settings could not be saved on close."); }
            ShutdownTask = Finish();
            main.Playback.Pause(); main.Session.Load(originalScript); main.Session.Select(originalPosition);
        };
        async Task Finish() { try { await Service.Stop(); } finally { ownedProvider?.Dispose(); } }
    }
    private AssistantOptions Options() => new(FastModel.Text.Trim(), DeepModel.Text.Trim(), FastEffort.SelectedItem as string ?? "none", DeepEffort.SelectedItem as string ?? "medium", Deeper.IsChecked == true, ContextNotes.Text, allowGeneralGuidance) { QuietSuggestions = quietSuggestions };
    internal AnswerRequest? Ask(string question, bool automatic = false)
    {
        try
        {
            var options = Options(); settings.Save(options);
            var request = Service.Ask(question, options, context.Snapshot(), context.SessionId, automatic);
            Status.Text = request.Status; return request;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status.Text = ex.Message; return null; }
    }
    private void Received(TranscriptEntry entry)
    {
        if (Automatic.IsChecked != true || main.Voice.Running || main.Playback.Voice.Active) return;
        var question = QuestionGate.Detect(entry);
        if (question is null || pending.Any(q => QuestionGate.Normalize(q) == QuestionGate.Normalize(question))) return;
        if (pending.Count >= 3) { Status.Text = "Automatic question queue full; a question was skipped. You can type it manually."; return; }
        pending.Enqueue(question);
    }
    private void SessionChanged() { pending.Clear(); Service.CancelAll(); Service.Questions.Reset(); }
    private void Refresh()
    {
        if (closed) return;
        KeyStatus.Text = keys.Exists ? "OpenAI key configured · requests use your API account" : "OpenAI key needed · ask Codex to configure it";
        ConversationStatus.Text = $"{(context.SessionId == Guid.Empty ? "No live transcript yet. Enable live transcription when recording." : "Recent finalized conversation is available.")} Pending automatic questions: {pending.Count}. Automatic detection pauses while voice following is active.";
        UpdatePreview();
    }
    private void ServiceChanged()
    {
        if (closed) return;
        if (Service.Requests.LastOrDefault() is { } latest)
            Status.Text = latest.Status + (latest.FirstReadableMs is { } ms ? $" · first readable paragraph {ms / 1000:F2}s" : "");
        Refresh();
    }
    private void Updated(StreamAnswer answer)
    {
        if (closed) return;
        if (!rows.TryGetValue(answer.Id, out var row))
        {
            row = new ListBoxItem { Tag = answer, Content = new TextBlock { TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(6) };
            rows.Add(answer.Id, row); Answers.Items.Add(row);
        }
        ((TextBlock)row.Content).Text = answer.Title + "\n" + answer.Detail;
        main.Session.RefreshAnswer(answer); UpdatePreview();
    }
    private void UpdatePreview()
    { Preview.Text = Answers.SelectedItem is ListBoxItem { Tag: StreamAnswer answer } ? string.Join("\n\n", answer.Blocks.Select(b => b.Text)) : "Select an answer to preview it. Your current reader stays in place."; }
    internal async Task SelectAnswer(StreamAnswer answer)
    {
        var request = ++navigation; await main.StopListening();
        if (closed || request != navigation) return;
        if (main.Session.Answer is { } prior) prior.SavedPosition = main.Session.Position;
        main.Session.ShowAnswer(answer); if (main.Panels.Count == 0) main.AddReader();
        Answers.SelectedItem = rows[answer.Id];
    }
    private void Ask_Click(object sender, RoutedEventArgs e) => Ask(Question.Text);
    private void Cancel_Click(object sender, RoutedEventArgs e) { pending.Clear(); Service.CancelAll(); }
    private void Automatic_Changed(object sender, RoutedEventArgs e) { if (ready && Automatic.IsChecked != true) pending.Clear(); }
    private void Answers_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private async void Read_Click(object sender, RoutedEventArgs e) { if (Answers.SelectedItem is ListBoxItem { Tag: StreamAnswer answer }) await SelectAnswer(answer); }
    private async Task Navigate(int direction)
    {
        var list = Service.Inbox.Answers; if (list.Count == 0) return;
        var index = list.ToList().FindIndex(a => a == main.Session.Answer); await SelectAnswer(list[Math.Clamp(index + direction, 0, list.Count - 1)]);
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Navigate(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await Navigate(1);
    private void Return_Click(object sender, RoutedEventArgs e) => Close();
    private void Save_Click(object sender, RoutedEventArgs e)
    { try { settings.Save(Options()); Status.Text = "AI settings saved."; } catch (Exception) { Status.Text = "Unable to save. Check settings and folder access."; } }
    private void Markdown_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Markdown and text|*.md;*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 64000) throw new InvalidDataException();
            var text = File.ReadAllText(dialog.FileName); if (text.Length > 16000) throw new InvalidDataException();
            ContextNotes.Text = text; Status.Text = "Context loaded. It will be sent with your next question.";
        }
        catch (Exception) { Status.Text = "Could not load context. Choose a readable file of at most 16,000 characters."; }
    }
}
