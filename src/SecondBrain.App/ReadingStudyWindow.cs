using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class ReadingStudyWindow : Window
{
    private readonly MainWindow main;
    private readonly string directory;
    private readonly string originalEditor, originalScript;
    private readonly ReaderStyle originalStyle;
    private readonly double originalSpeed;
    private readonly int originalPosition;
    private readonly List<(ReaderWindow Panel, PanelPlacement Placement)> originalPanels;
    private readonly List<ReadingResult> results;
    private readonly ComboBox round = new() { ItemsSource = new[] { 1, 2 }, SelectedIndex = 0 };
    private readonly ComboBox trialNumber = new() { ItemsSource = new[] { 1, 2, 3, 4, 5 }, SelectedIndex = 0 };
    private readonly ComboBox preferredWidth = new() { ItemsSource = new[] { 28, 36, 48 }, SelectedIndex = 1 };
    private readonly ComboBox preferredFont = new() { ItemsSource = new[] { 32, 38 }, SelectedIndex = 0 };
    private readonly ComboBox answer = new();
    private readonly TextBlock question = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 12, 0, 0) };
    private readonly CheckBox completed = new() { Content = "I read the passage and completed this trial", Margin = new(0, 12, 0, 12) };
    private readonly TextBox mistakes = new() { Text = "0" }, corrections = new() { Text = "0" }, lost = new() { Text = "0" };
    private readonly ComboBox comfort = new() { ItemsSource = new[] { 1, 2, 3, 4, 5 }, SelectedIndex = 2 };
    private readonly ComboBox strain = new() { ItemsSource = new[] { 1, 2, 3, 4, 5 }, SelectedIndex = 0 };
    private readonly Stopwatch elapsed = new();
    private ReadingTrial? active;
    private bool keepPreset;
    private double acceptedSpeed;

    public ReadingStudyWindow(MainWindow main, string directory)
    {
        this.main = main; this.directory = directory;
        originalEditor = main.ScriptEditor.Text; originalScript = main.Session.Text;
        originalStyle = main.Session.Style; originalSpeed = main.Playback.WordsPerMinute;
        originalPosition = main.Session.Position;
        originalPanels = main.Panels.Select(p => (p, NativeWindows.GetBounds(p))).ToList();
        results = LoadResults();
        preferredWidth.SelectedItem = ReadingStudy.RecommendedWidth(results);
        preferredFont.SelectedItem = ReadingStudy.RecommendedFont(results);
        var pending = Enumerable.Range(1, 2).SelectMany(s => Enumerable.Range(1, 5).Select(t => (Session: s, Trial: t)))
            .FirstOrDefault(p => !results.Any(r => r.Session == p.Session && r.Trial == p.Trial));
        if (pending.Session > 0) { round.SelectedItem = pending.Session; trialNumber.SelectedItem = pending.Trial; }
        Title = "Reading comparison"; Width = 600; Height = 760; MinWidth = 440; MinHeight = 420;
        Style = (Style)Application.Current.FindResource("ShellWindowStyle"); Owner = main;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new(24) };
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(new TextBlock { Text = "Find your comfortable layout", FontSize = 23, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Keep the camera position and viewing distance fixed. Read aloud using Play timed; adjust WPM to a comfortable pace. Do trials 1–3, then compare fonts in 4–5 at your preferred width. Repeat in a separate second session. The order changes between sessions. Closing restores your script.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 16) });
        Add("Session", round); Add("Trial", trialNumber); Add("Preferred width for font trials", preferredWidth);
        Button("Load trial", async () => await LoadTrial());
        content.Children.Add(question); content.Children.Add(answer);
        Add("Reading mistakes / skipped phrases", mistakes); Add("Manual corrections", corrections); Add("Lost-place events", lost);
        Add("Reading comfort (1 low, 5 high)", comfort); Add("Eye strain (1 low, 5 high)", strain);
        content.Children.Add(completed);
        Button("Save result", SaveResult);
        content.Children.Add(summary); UpdateSummary();
        Add("Preferred font size", preferredFont);
        Button("Accept and keep this preset", AcceptPreset);
        content.Children.Add(status);
        status.Text = $"{results.Select(r => (r.Session, r.Trial)).Distinct().Count()} of 10 trials saved. Results are kept beside the app in data/reading-study.";
        Closed += (_, _) =>
        {
            main.Playback.Pause();
            main.Session.Load(originalScript); main.Session.Select(originalPosition); main.ScriptEditor.Text = originalEditor;
            SetStyle(keepPreset ? new ReaderStyle(ColumnCharacters: (int)preferredWidth.SelectedItem, FontSize: (int)preferredFont.SelectedItem) : originalStyle);
            main.SpeedSlider.Value = keepPreset ? acceptedSpeed : originalSpeed;
            if (!keepPreset) foreach (var (panel, placement) in originalPanels) if (main.Panels.Contains(panel)) NativeWindows.Restore(panel, placement);
        };
        void Add(string label, FrameworkElement field)
        { content.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap }); field.MinHeight = 28; content.Children.Add(field); }
        void Button(string label, Action action)
        { var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 10) }; button.Click += (_, _) => action(); content.Children.Add(button); }
    }
    private List<ReadingResult> LoadResults()
    {
        var path = Path.Combine(directory, "results.json");
        if (!File.Exists(path)) return [];
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ReadingResult>>(File.ReadAllText(path)) ?? [];
            if (loaded.Any(r => r is null || r.Session is < 1 or > 2 || r.Trial is < 1 or > 5 || r.Width is not (28 or 36 or 48)
                || r.Font is not (32 or 38) || r.Comfort is < 1 or > 5 || r.EyeStrain is < 1 or > 5 || r.Mistakes < 0 || r.Corrections < 0 || r.LostPlace < 0))
                throw new JsonException("Invalid trial result.");
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Saved reading results could not be loaded. Your existing file has been preserved.", ex); }
    }
    private void SetStyle(ReaderStyle style)
    {
        main.FontSlider.Value = style.FontSize; main.WidthSlider.Value = style.ColumnCharacters;
        main.SpacingSlider.Value = style.LineSpacing; main.OpacitySlider.Value = style.BackgroundOpacity * 100; main.BandSlider.Value = style.ReadingBand * 100;
    }
    private async Task LoadTrial()
    {
        main.Playback.Pause(); await main.StopListening();
        if (!IsVisible) return;
        active = ReadingStudy.Trial((int)round.SelectedItem, (int)trialNumber.SelectedItem, (int)preferredWidth.SelectedItem);
        main.ScriptEditor.Text = active.Text; await main.ApplyText();
        SetStyle(new(FontSize: active.Font, ColumnCharacters: active.Width));
        if (main.Panels.Count == 0) main.AddReader();
        foreach (var panel in main.Panels)
        {
            panel.Width = Math.Max(panel.ActualWidth, active.Width * active.Font * .5 + 66);
            panel.Height = Math.Max(panel.ActualHeight, active.Font * 1.4 * 6 + 121);
            NativeWindows.Restore(panel, NativeWindows.GetBounds(panel));
        }
        await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (!IsVisible || active is null) return;
        if (main.Panels.Any(p => p.ScriptText.ActualWidth < active.Width * active.Font * .5 - 1))
        { status.Text = "This display cannot fit the requested comparison width. Move the reader to a larger display, then reload the trial. This trial cannot be saved yet."; active = null; return; }
        question.Text = active.Question; answer.ItemsSource = active.Answers; answer.SelectedIndex = -1;
        mistakes.Text = corrections.Text = lost.Text = "0"; completed.IsChecked = false; elapsed.Restart();
        comfort.SelectedIndex = strain.SelectedIndex = -1;
        status.Text = $"Session {active.Session}, trial {active.Number}: {active.Width} characters, {active.Font} px. Press Play timed in the reader when ready. Return here to record the result.";
    }
    private void SaveResult()
    {
        if (active is null || completed.IsChecked != true || answer.SelectedIndex < 0 || comfort.SelectedIndex < 0 || strain.SelectedIndex < 0)
        { status.Text = "Complete a trial, answer the question, and choose comfort and eye-strain ratings before saving."; return; }
        if (!int.TryParse(mistakes.Text, out var errors) || !int.TryParse(corrections.Text, out var fixes) || !int.TryParse(lost.Text, out var lostCount) || errors < 0 || fixes < 0 || lostCount < 0)
        { status.Text = "Enter zero or a positive whole number for each count."; return; }
        if (main.Session.Text != active.Text || main.Session.Style.FontSize != active.Font || main.Session.Style.ColumnCharacters != active.Width
            || main.Panels.Count == 0 || main.Panels.Any(p => p.ScriptText.ActualWidth < active.Width * active.Font * .5 - 1))
        { status.Text = "The trial text or layout changed. Reload the trial before saving a comparable result."; return; }
        main.Playback.Pause();
        var result = new ReadingResult(DateTimeOffset.UtcNow, active.Session, active.Number, active.Kind, active.Width, active.Font,
            main.Playback.WordsPerMinute, errors, fixes, lostCount, (int)comfort.SelectedItem, (int)strain.SelectedItem,
            answer.SelectedIndex == active.CorrectAnswer, elapsed.Elapsed.TotalSeconds);
        try
        {
            var updated = results.Where(r => r.Session != active.Session || r.Trial != active.Number).Append(result).ToList();
            Write("results.json", updated); results.Clear(); results.AddRange(updated);
            preferredWidth.SelectedItem = ReadingStudy.RecommendedWidth(results);
            preferredFont.SelectedItem = ReadingStudy.RecommendedFont(results); UpdateSummary();
            status.Text = "Saved. " + (result.ComprehensionCorrect ? "Comprehension answer correct." : "Comprehension answer incorrect.") + " Suggested width: " + preferredWidth.SelectedItem + ". Select the next trial when ready.";
            active = null; completed.IsChecked = false;
            if (trialNumber.SelectedIndex < 4) trialNumber.SelectedIndex++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "Result could not be saved. Check the data folder and retry."; }
    }
    private void AcceptPreset()
    {
        if (!ReadingStudy.Complete(results)) { status.Text = "Complete all five trials in both sessions first. You can adjust the reader freely before accepting a preset."; return; }
        try
        {
            Write("accepted-preset.json", new { acceptedAt = DateTimeOffset.UtcNow, width = (int)preferredWidth.SelectedItem, font = (int)preferredFont.SelectedItem,
                wordsPerMinute = main.Playback.WordsPerMinute, basis = "User selection after two sessions; errors/corrections first, comfort as tie-breaker", trialCount = results.Count });
            acceptedSpeed = main.Playback.WordsPerMinute; keepPreset = true; Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "Preset could not be saved. Check the data folder and retry."; }
    }
    private void UpdateSummary() => summary.Text = string.Join("\n", results.OrderBy(r => r.Session).ThenBy(r => r.Trial)
        .Select(r => $"Session {r.Session}, trial {r.Trial}: {r.Width} ch / {r.Font} px · {r.Mistakes} errors, {r.Corrections} corrections · comfort {r.Comfort}/5 · {(r.ComprehensionCorrect ? "quiz correct" : "quiz incorrect")}"));
    private void Write(string name, object value)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    internal async Task VerifyPersistence(Action<bool, string> check)
    {
        await LoadTrial();
        check(main.Session.Text == active!.Text && main.Session.Style.ColumnCharacters == 36, "Loading trial applies the matched passage and planned width");
        check(main.Panels.All(p => p.ScriptText.ActualWidth >= 36 * 32 * .5 - 1), "Comparison panel is widened so the requested column is not silently clamped");
        completed.IsChecked = true; answer.SelectedIndex = active.CorrectAnswer;
        SaveResult(); check(!File.Exists(Path.Combine(directory, "results.json")), "Unanswered comfort ratings cannot silently become study results");
        comfort.SelectedIndex = 2; strain.SelectedIndex = 0;
        mistakes.Text = "2"; corrections.Text = "1"; SaveResult();
        var saved = JsonSerializer.Deserialize<List<ReadingResult>>(File.ReadAllText(Path.Combine(directory, "results.json")))!;
        check(saved.Count == 1 && saved[0].Mistakes == 2 && saved[0].Corrections == 1 && saved[0].ComprehensionCorrect, "Trial metrics and comprehension persist in local JSON");
        AcceptPreset();
        check(!File.Exists(Path.Combine(directory, "accepted-preset.json")), "Incomplete study cannot be marked accepted");
        await LoadTrial(); completed.IsChecked = true; answer.SelectedIndex = active!.CorrectAnswer; comfort.SelectedIndex = 2; strain.SelectedIndex = 0;
        main.WidthSlider.Value = 48; SaveResult();
        check(JsonSerializer.Deserialize<List<ReadingResult>>(File.ReadAllText(Path.Combine(directory, "results.json")))!.Count == 1, "Changed trial geometry cannot be saved as the assigned comparison");
        trialNumber.SelectedItem = 3; await LoadTrial();
        check(active is not null && main.Panels.All(p => p.ScriptText.ActualWidth >= 48 * 32 * .5 - 1), "Wide comparison renders the full 48-character target");
    }
}
