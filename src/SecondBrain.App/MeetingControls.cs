using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private sealed record MeetingRow(string Path, string Label);
    private Task meetingsRefresh = Task.CompletedTask;
    private bool compactNavigation;
    private void AudioSettings_Click(object sender, RoutedEventArgs e)
    { SettingsTab.IsSelected = true; DevicesTab.IsSelected = true; }
    private void NavigationToggle_Click(object sender, RoutedEventArgs e)
    {
        compactNavigation = !compactNavigation;
        var pages = new[] { (LiveTab, "Live", "●"), (MeetingsTab, "Meetings", "▣"), (KnowledgeTab, "Knowledge", "⌕"), (SettingsTab, "Settings", "⚙") };
        foreach (var (tab, name, icon) in pages)
        {
            tab.Header = compactNavigation ? icon : name;
            tab.MinWidth = compactNavigation ? 42 : 112;
        }
        NavigationToggle.Content = compactNavigation ? "Expand menu" : "Compact menu";
    }
    private async void Workspace_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || e.Source != WorkspaceTabs || !MeetingsTab.IsSelected) return;
        await RefreshMeetings();
    }
    private async void MeetingsRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMeetings();
    internal Task RefreshMeetings() => !meetingsRefresh.IsCompleted ? meetingsRefresh : meetingsRefresh = LoadMeetings();
    private async Task LoadMeetings()
    {
        if (closing) return;
        var selected = (MeetingList.SelectedItem as MeetingRow)?.Path;
        MeetingListStatus.Text = "Loading saved meetings…";
        try
        {
            var rows = await Task.Run(() => LocalBackup.Recordings(dataDirectory).Select(path =>
            {
                try
                {
                    var meeting = RecordingSession.ReadManifest(path);
                    var elapsed = TimeSpan.FromSeconds(Math.Max(0, meeting.DurationSeconds));
                    string? client = null;
                    try { var brief = SessionContextStore.ReadSnapshot(path); if (brief?.SessionId == meeting.Id) client = brief.Context.Client; } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
                    return new MeetingRow(path, $"{meeting.StartedUtc.ToLocalTime():ddd, MMM d · HH:mm}   ·   {elapsed:hh\\:mm\\:ss}   ·   {meeting.State}" + (string.IsNullOrWhiteSpace(client) ? "" : "   ·   " + client));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                { return new MeetingRow(path, Path.GetFileName(path) + " · Details unavailable"); }
            }).ToArray());
            if (closing) return;
            MeetingList.ItemsSource = rows;
            MeetingList.SelectedItem = rows.FirstOrDefault(r => r.Path == selected) ?? rows.FirstOrDefault();
            MeetingListStatus.Text = rows.Length == 0 ? "No saved meetings yet. Start listening on the Live page to begin." : $"{rows.Length} saved meeting{(rows.Length == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MeetingListStatus.Text = "Meetings could not be loaded. Check access to the recording folder."; }
    }
    private void MeetingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is not MeetingRow row) { MeetingListStatus.Text = "Select a meeting first."; return; }
        try { Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true }); }
        catch (Exception) { MeetingListStatus.Text = "The meeting folder could not be opened. Refresh the list and try again."; }
    }
    private async void MeetingTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is not MeetingRow row) { MeetingListStatus.Text = "Select a meeting first."; return; }
        await OpenTranscript(row.Path);
    }
    internal async Task<TranscriptWindow?> OpenTranscript(string path)
    {
        try
        {
            var loaded = await Task.Run(() =>
            {
                try { return (Records: TranscriptDetails.Read(path), Warning: "Select a segment to inspect its word times."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { return (Records: TranscriptDetails.Read(path, false), Warning: "Word details unavailable or damaged; showing original transcript text."); }
            });
            if (closing) return null;
            var window = new TranscriptWindow(this, loaded.Records, loaded.Warning, hiddenTestMode); window.Show(); return window;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { MeetingListStatus.Text = "No readable transcript is available for this recording. Its audio remains in the meeting folder."; return null; }
    }
}
