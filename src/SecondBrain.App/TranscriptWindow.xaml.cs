using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class TranscriptWindow : Window
{
    private readonly string directory;
    private readonly bool hidden;
    private readonly TranscriptDetail[] records;
    private TranscriptReview review;
    private bool ready, editable = true;
    private WasapiOut? output;
    private WaveFileReader? audio;
    private double replayEnd;
    private readonly DispatcherTimer replayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    internal TranscriptReview Review => review;
    internal TranscriptWindow(MainWindow owner, string directory, TranscriptDetail[] records, string warning, bool hidden)
    {
        this.directory = directory; this.records = records; this.hidden = hidden;
        InitializeComponent(); Owner = owner;
        if (hidden) { Opacity = 0; ShowActivated = false; }
        try { review = new(directory, records); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { review = new(directory, records, false); editable = false; warning += " Correction journal unavailable; original transcript shown read-only."; }
        Summary.Text = $"{records.Length} finalized segments · {records.Sum(r => r.Words.Length)} timed words. Original transcript text is read-only. " + warning + " " + review.HintWarning;
        EditControls.IsEnabled = BookmarkButton.IsEnabled = editable;
        ready = true; Refresh();
        replayTimer.Tick += (_, _) => { if (audio is null || audio.CurrentTime.TotalSeconds >= replayEnd) StopPlayback(); };
        Closed += (_, _) => StopPlayback();
    }
    private void Refresh()
    {
        var selected = (Segments.SelectedItem as ReviewTurn)?.Words[0].Id;
        var rows = review.Turns(Search.Text, BookmarksOnly.IsChecked == true);
        Segments.ItemsSource = rows; Segments.SelectedItem = rows.FirstOrDefault(t => t.Words.Any(w => w.Id == selected)) ?? rows.FirstOrDefault();
        MergeTarget.ItemsSource = review.Words.Where(w => w.SpeakerId is not null).DistinctBy(w => w.SpeakerId).ToArray();
        UndoButton.IsEnabled = editable && review.CanUndo;
        if (rows.Length == 0) { WordSelection.ItemsSource = null; WordDetails.Text = "No matching turns."; }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (ready) Refresh(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (ready) Refresh(); }
    private void Segments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Segments.SelectedItem is not ReviewTurn row) return;
        WordSelection.ItemsSource = row.Words; WordSelection.SelectedIndex = 0;
        WordDetails.Text = string.Join("\n", row.Words.Select(w => w.Timed ? $"{w.Start:F3}–{w.End:F3}s · {w.Text} · {w.Speaker}"
            : "Word timing unavailable for this segment · " + w.Speaker));
        SpeakerName.Text = row.Words[0].Speaker == "Unknown" ? "" : row.Words[0].Speaker;
    }
    private ReviewTurn Selected() => Segments.SelectedItem as ReviewTurn ?? throw new InvalidOperationException("Select a transcript turn first.");
    private string Speaker() => Selected().Words[0].SpeakerId ?? throw new InvalidOperationException("Name this turn first; Unknown does not identify one person.");
    private void Edit(Action action)
    {
        if (!editable) return;
        try { action(); Refresh(); ReviewStatus.Text = "Saved locally · original transcript unchanged · Undo is available."; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ReviewStatus.Text = ex.Message; }
    }
    private void Assign_Click(object sender, RoutedEventArgs e) => Edit(() => review.Assign(Selected().Words.Select(w => w.Id), SpeakerName.Text));
    private void Rename_Click(object sender, RoutedEventArgs e) => Edit(() => review.Rename(Speaker(), SpeakerName.Text));
    private void Split_Click(object sender, RoutedEventArgs e) => Edit(() => review.Assign(WordSelection.SelectedItems.Cast<ReviewWord>().Select(w => w.Id), SpeakerName.Text));
    private void Merge_Click(object sender, RoutedEventArgs e) => Edit(() => review.Merge(Speaker(), (MergeTarget.SelectedItem as ReviewWord)?.SpeakerId ?? throw new InvalidOperationException("Choose a destination speaker.")));
    private void Bookmark_Click(object sender, RoutedEventArgs e) => Edit(() => review.Bookmark(Selected().Words.FirstOrDefault(w => review.Bookmarks.Contains(w.Id))?.Id ?? Selected().Words[0].Id));
    private void Undo_Click(object sender, RoutedEventArgs e) => Edit(review.Undo);
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try { var loaded = new TranscriptReview(directory, records); review = loaded; editable = true; EditControls.IsEnabled = BookmarkButton.IsEnabled = true; Refresh(); ReviewStatus.Text = "Review reloaded."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { ReviewStatus.Text = "Review could not reload: " + ex.Message; }
    }
    internal double ReplaySelection()
    {
        StopPlayback(); var row = Selected(); var word = WordSelection.SelectedItem as ReviewWord ?? row.Words[0];
        var path = LocalBackup.Root(Path.Combine(directory, word.Source == AudioSource.Microphone ? "microphone.wav" : "system.wav"));
        try
        {
            audio = new WaveFileReader(path);
            if (word.Start >= audio.TotalTime.TotalSeconds) throw new InvalidDataException("This timestamp is beyond the saved audio.");
            audio.CurrentTime = TimeSpan.FromSeconds(word.Start); replayEnd = Math.Min(audio.TotalTime.TotalSeconds, row.Words.Max(w => w.End));
            ReviewStatus.Text = $"Playing {word.Source} from {word.Start:F2}s · stops at this turn's end.";
            if (!hidden) { output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100); output.Init(audio); output.Play(); replayTimer.Start(); }
            return audio.CurrentTime.TotalSeconds;
        }
        catch { StopPlayback(); throw; }
    }
    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        try { ReplaySelection(); }
        catch (Exception) { ReviewStatus.Text = "Saved audio or playback device is unavailable. Stop/save an active recording first, or check the audio file."; }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { StopPlayback(); ReviewStatus.Text = "Playback stopped."; }
    private void StopPlayback()
    {
        replayTimer.Stop();
        var player = output; output = null; var source = audio; audio = null;
        try { player?.Stop(); } catch (Exception) { ReviewStatus.Text = "Playback device stopped responding."; }
        try { player?.Dispose(); } catch (Exception) { ReviewStatus.Text = "Playback device could not close cleanly."; }
        source?.Dispose();
    }
}
