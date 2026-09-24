using System.IO;
using System.Windows;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    internal CompanionSession? Companion { get; private set; }
    private readonly SemaphoreSlim companionControls = new(1);
    private bool companionBusy;
    private string companionPhase = "WORKING";
    private bool companionError;
    private AssistantSettings? companionSettings;
    private void InitializeCompanion()
    {
        companionSettings = new(dataDirectory);
        LiveFastEffort.ItemsSource = LiveDeepEffort.ItemsSource = new[] { "none", "low", "medium", "high" };
        AssistantOptions options = new();
        try { options = companionSettings.Load(); } catch (Exception) { CompanionStatus.Text = "AI settings could not be loaded; review and save the defaults."; }
        LiveFastModel.Text = options.FastModel; LiveDeepModel.Text = options.DeepModel;
        LiveFastEffort.SelectedItem = options.FastEffort; LiveDeepEffort.SelectedItem = options.DeepEffort; LiveContext.Text = options.Context;
        RefreshOutputDevices();
        if (!hiddenTestMode) Session.ShowAnswer(new AnswerInbox().Begin(Guid.NewGuid(), Guid.NewGuid(), "Start listening to begin your companion session."));
    }
    private AssistantOptions CompanionOptions() => new(LiveFastModel.Text.Trim(), LiveDeepModel.Text.Trim(),
        LiveFastEffort.SelectedItem as string ?? "none", LiveDeepEffort.SelectedItem as string ?? "medium", true, LiveContext.Text);
    private void RefreshOutputDevices()
    {
        try
        {
            var selected = (LiveOutputPicker.SelectedItem as AudioDevice)?.Id ?? settings.RecordingOutputId;
            var devices = hiddenTestMode ? new AudioDevice[] { new("test-output", "Synthetic output") } : AudioCaptureSource.Devices(AudioSource.System);
            LiveOutputPicker.ItemsSource = devices;
            LiveOutputPicker.SelectedItem = devices.FirstOrDefault(d => d.Id == selected) ?? (selected is null ? devices.FirstOrDefault() : null);
            if (hiddenTestMode) LiveOutputPicker.SelectedIndex = 0;
        }
        catch (Exception) { CompanionStatus.Text = "Computer audio devices could not be listed. Refresh and select your meeting output."; }
    }
    private async void Listen_Click(object sender, RoutedEventArgs e)
    { if (Companion?.Active == true) await StopCompanion(); else await StartCompanion(); }
    internal async Task<bool> StartCompanion(IAnswerProvider? testProvider = null)
    {
        await companionControls.WaitAsync(); companionBusy = true; companionError = false; companionPhase = "STARTING"; RefreshCompanionControls();
        try
        {
            if (closing || Companion?.Active == true) return false;
            if (MicrophonePicker.SelectedItem is not Microphone mic || LiveOutputPicker.SelectedItem is not AudioDevice output)
                throw new InvalidOperationException("Choose an available microphone and the output your meeting plays through.");
            var baseOptions = CompanionOptions(); var preview = ReadMeetingContext();
            var options = baseOptions with { Context = preview.CombineWith(baseOptions.Context) };
            if (!options.IsValid) throw new InvalidOperationException("Check the models and meeting context before starting.");
            companionSettings!.Save(baseOptions);
            if (!hiddenTestMode) { _ = new ApiKeyStore(dataDirectory).Load(); _ = new ApiKeyStore(dataDirectory, "OpenAI").Load(); }
            var meetingContext = SaveMeetingContext();
            if (Knowledge?.ForProject(meetingContext.Project) is IStagedKnowledgeSearch prepared)
                _ = prepared.Prewarm(meetingContext.Goal, CancellationToken.None);
            Assistant?.Close(); StreamDemo?.Close(); study?.Close(); replay?.Stop();
            await StopListening(); await Recorder.StopAsync();
            if (recordingWindow is { IsVisible: true }) recordingWindow.Close();
            Transcriber.DrainAssistantEvents(out _); Transcriber.DrainSpeech();
            Companion?.Dispose();
            var provider = testProvider ?? new OpenAiAnswerProvider(new ApiKeyStore(dataDirectory, "OpenAI").Load);
            Companion = new(Session, Playback, AssistantContext, new(Dispatcher, provider, log, Knowledge?.ForProject(meetingContext.Project)), options, testProvider is null ? provider as IDisposable : null, () => Recorder.ClockOrigin);
            Companion.KeepFlowing = FlowCheck.IsChecked == true;
            companionVaultRoot = Knowledge?.Root;
            sourcesRequest = Guid.Empty; LiveSources.Items.Clear(); SourceStatus.Text = "Waiting for retrieved sources.";
            Companion.Changed += RefreshCompanion;
            SaveRecordingDevices(mic.Id, output.Id); Transcriber.Enabled = true;
            if (Panels.Count == 0) AddReader();
            WorkspaceTabs.SelectedIndex = 0; LiveMic.Text = "Microphone · waiting"; LiveSystem.Text = "Computer audio · waiting";
            CompanionStatus.Text = "Starting recording, transcription and automatic answers…";
            await Recorder.StartAsync(mic.Id, output.Id);
            if (Recorder.State != RecordingState.Recording) throw new InvalidOperationException(Recorder.Message);
            var recordingDirectory = Recorder.LastDirectory ?? throw new IOException("Recording directory is unavailable.");
            SessionContextStore.SaveSnapshot(recordingDirectory, RecordingSession.ReadManifest(recordingDirectory).Id, meetingContext);
            MeetingBriefExpander.IsExpanded = false;
            if (LiveTab.Content is System.Windows.Controls.ScrollViewer liveView) liveView.ScrollToTop();
            RefreshCompanion(); return true;
        }
        catch (Exception ex)
        {
            companionError = true;
            if (Companion is { } companion) { await companion.Stop(); companion.Changed -= RefreshCompanion; }
            await Recorder.StopAsync();
            CompanionStatus.Text = ex is InvalidOperationException or IOException ? ex.Message : "The session could not start. Check settings and audio devices.";
            log.Write("Companion startup failure=" + ex.GetType().Name); return false;
        }
        finally { companionBusy = false; companionControls.Release(); RefreshCompanionControls(); }
    }
    internal async Task StopCompanion()
    {
        await companionControls.WaitAsync(); companionBusy = true; companionPhase = "SAVING"; RefreshCompanionControls();
        try
        {
            if (Companion is { Active: true } companion)
            {
                var answers = companion.Stop(); // Disable new questions before flushing transcript finals.
                await Recorder.StopAsync(); await answers;
                CompanionStatus.Text = Recorder.State == RecordingState.Completed ? "Stopped · audio and transcripts saved. Your answer stays in the reader." : Recorder.Message;
                await ExportCurrentMeeting();
                if (Recorder.LastDirectory is { } folder && !await SaveCompanionTiming(folder))
                    CompanionStatus.Text += " Timing report could not be saved; audio is unaffected.";
            }
            Playback.Pause(); RefreshCompanionControls();
        }
        finally { companionBusy = false; companionControls.Release(); RefreshCompanionControls(); }
    }
    private void DrainCompanion()
    {
        var entries = Transcriber.DrainAssistantEvents(out var dropped);
        foreach (var entry in entries.Where(e => e.Kind == "RunStart")) AssistantContext.Observe(entry);
        var speeches = Transcriber.DrainSpeech();
        foreach (var speech in speeches)
        {
            if (speech.Source == AudioSource.Microphone) Companion?.Observe(speech);
            if (Companion?.Active == true && speech.Segment.Text.Length > 0)
            {
                if (speech.Source == AudioSource.Microphone) LiveMic.Text = "Microphone · " + speech.Segment.Text;
                else LiveSystem.Text = "Computer audio · " + speech.Segment.Text;
            }
        }
        foreach (var entry in entries.Where(e => e.Kind != "RunStart")) AssistantContext.Observe(entry);
        if (dropped) AssistantContext.MarkGap();
        foreach (var speech in speeches.Where(s => s.Source == AudioSource.System)) Companion?.Observe(speech);
        Companion?.Tick();
        TickKnowledge();
        if (Companion?.Active == true)
        {
            CaptureStatus.Text = $"{TimeSpan.FromSeconds(Recorder.Elapsed):hh\\:mm\\:ss} · {Recorder.Message}\n{Transcriber.View.Status}";
            if (Recorder.State == RecordingState.Failed && !companionBusy) _ = StopCompanion();
        }
    }
    private void RefreshCompanionControls()
    {
        if (!initialized) return;
        var active = Companion?.Active == true;
        SessionStateText.Text = companionBusy ? companionPhase : active ? "LISTENING" : companionError || Recorder.State == RecordingState.Failed ? "ATTENTION" : "READY";
        SessionStateBadge.Background = (System.Windows.Media.Brush)FindResource(active ? "Accent" : "Subtle");
        ListenButton.IsEnabled = !companionBusy && !closing;
        ListenButton.Content = companionBusy ? "Please wait…" : active ? "Stop listening and save" : "Start listening";
        MicrophonePicker.IsEnabled = RefreshMicrophonesButton.IsEnabled = LiveOutputPicker.IsEnabled = !active && !companionBusy;
        PracticeTab.IsEnabled = !active && !companionBusy;
        DiagnosticControls.IsEnabled = !active && !companionBusy;
        LiveAsk.IsEnabled = active && !companionBusy;
        ClientPicker.IsEnabled = ContextEditor.IsEnabled = !active && !companionBusy && !contextLoadFailed;
    }
    private void RefreshCompanion()
    {
        if (Companion is not { } companion) return;
        CompanionStatus.Text = companion.Status;
        CurrentQuestion.Text = companion.Selected?.Title ?? "Waiting for a question from computer audio";
        var text = companion.Selected is { } answer ? string.Join("\n\n", answer.Blocks.Select(b => b.Text)) : "Your first answer will appear here and in the floating reader automatically.";
        if (LiveAnswer.Text != text) LiveAnswer.Text = text;
        RefreshCompanionControls();
        RefreshSources();
        RefreshLatency();
    }
    private void LiveAsk_Click(object sender, RoutedEventArgs e) => Companion?.Ask(LiveQuestion.Text);
    private void FlowCheck_Click(object sender, RoutedEventArgs e)
    { if (Companion is { } companion) companion.KeepFlowing = FlowCheck.IsChecked == true; }
    private void CompanionPrevious_Click(object sender, RoutedEventArgs e) => Companion?.Navigate(-1);
    private void CompanionNext_Click(object sender, RoutedEventArgs e) => Companion?.Navigate(1);
    private void ResumeFollowing_Click(object sender, RoutedEventArgs e)
    { if (Companion?.Active == true && Companion.Selected is not null) { Playback.StartVoice(); CompanionStatus.Text = "Following resumed · recording and answers remain active."; } }
    private void LiveSave_Click(object sender, RoutedEventArgs e)
    {
        try { companionSettings!.Save(CompanionOptions()); CompanionStatus.Text = "Settings saved for the next session."; }
        catch (Exception) { CompanionStatus.Text = "Could not save. Check model names, context size and folder access."; }
    }
    private void LiveMarkdown_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Markdown and text|*.md;*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 64000) throw new InvalidDataException();
            var text = File.ReadAllText(dialog.FileName); if (text.Length > 16000) throw new InvalidDataException();
            LiveContext.Text = text; companionSettings!.Save(CompanionOptions()); CompanionStatus.Text = "Context loaded and saved for the next session.";
        }
        catch (Exception) { CompanionStatus.Text = "Choose a readable context file of at most 16,000 characters."; }
    }
}
