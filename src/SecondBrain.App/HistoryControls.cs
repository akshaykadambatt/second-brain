using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    internal MaintenanceService? Maintenance { get; private set; }
    private Task historyConnection = Task.CompletedTask;
    private bool historyUiBusy;
    private long historyRevision = -1;
    private async Task ConnectMaintenance(string root, bool enabled)
    {
        try
        {
            var old = Maintenance; Maintenance = null;
            if (old is not null) { await old.Stop(); old.Dispose(); }
            if (closing) return;
            Maintenance = new(root, hiddenTestMode ? new EmptyMaintenance() : new OpenAiMaintenance(new ApiKeyStore(dataDirectory, "OpenAI").Load, new AssistantSettings(dataDirectory).Load), enabled);
            HistoryRevisions.Items.Clear(); HistoryUpdates.Items.Clear(); HistoryDiff.Text = "Select a revision to inspect its changes."; historyRevision = -1;
        }
        catch (Exception) { MaintenanceStatus.Text = "Local history could not open. Check vault access and Git for Windows, then Apply vault to retry. The reader remains available."; }
    }
    private void TickMaintenance()
    {
        if (Maintenance is not { } history || closing) return;
        MaintenanceStatus.Text = history.Status;
        HistoryUndo.IsEnabled = HistoryRetry.IsEnabled = HistoryRecover.IsEnabled = HistoryScan.IsEnabled = !historyUiBusy && !history.Busy;
        if (!history.Busy && !historyUiBusy && historyRevision != history.Revision) { historyRevision = history.Revision; _ = RefreshHistory(); }
    }
    private async Task RefreshHistory()
    {
        if (historyUiBusy || Maintenance is not { } history) return;
        historyUiBusy = true;
        try
        {
            var view = await history.ReadHistory(); if (Maintenance != history || closing) return;
            var selectedCommit = (HistoryRevisions.SelectedItem as ListBoxItem)?.Tag as string;
            var selectedId = (HistoryUpdates.SelectedItem as ListBoxItem)?.Tag as Guid?;
            HistoryRevisions.Items.Clear();
            foreach (var line in view.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            { var item = new ListBoxItem { Content = line, Tag = line.Split(' ')[0] }; HistoryRevisions.Items.Add(item); if ((string)item.Tag == selectedCommit) item.IsSelected = true; }
            HistoryUpdates.Items.Clear();
            foreach (var update in view.Updates)
            { var item = new ListBoxItem { Content = $"{update.Date} · {update.State} · {update.Sections.Length} notes · {update.MeetingId.ToString("N")[..8]}", Tag = update.MeetingId }; HistoryUpdates.Items.Add(item); if (update.MeetingId == selectedId) item.IsSelected = true; }
            HistoryJobs.Text = string.Join("\n", view.Jobs.TakeLast(12).Select(j => j.State + " · " + j.Message));
        }
        catch (Exception ex) { HistoryMessage.Text = "History unavailable: " + ex.Message; }
        finally { historyUiBusy = false; }
    }
    private async void HistoryRefresh_Click(object sender, RoutedEventArgs e) => await RefreshHistory();
    private async void HistoryDiff_Click(object sender, RoutedEventArgs e)
    {
        if (Maintenance is not { } history || HistoryRevisions.SelectedItem is not ListBoxItem { Tag: string commit } || historyUiBusy) return;
        historyUiBusy = true;
        try { var diff = await history.Diff(commit); if (Maintenance == history) HistoryDiff.Text = diff; }
        catch (Exception ex) { HistoryMessage.Text = ex.Message; }
        finally { historyUiBusy = false; }
    }
    private async void HistoryUndo_Click(object sender, RoutedEventArgs e)
    {
        if (Maintenance is not { } history || HistoryUpdates.SelectedItem is not ListBoxItem { Tag: Guid id } || historyUiBusy) return;
        historyUiBusy = true;
        try { await history.Revert(id); HistoryMessage.Text = "Update reverted. Later unrelated edits remain. A new history entry records the undo."; if (Knowledge is { } knowledge) _ = knowledge.Refresh(true); }
        catch (Exception ex) { HistoryMessage.Text = ex.Message; }
        finally { historyUiBusy = false; await RefreshHistory(); }
    }
    private void HistoryRetry_Click(object sender, RoutedEventArgs e) { Maintenance?.Retry(); HistoryMessage.Text = "Pending/failed updates queued for another attempt."; }
    private void HistoryScan_Click(object sender, RoutedEventArgs e)
    { try { Maintenance?.QueueSavedMeetings(true); Maintenance?.Retry(); HistoryMessage.Text = "Saved meetings queued. Applied or reverted meetings will not be duplicated."; } catch (Exception ex) { HistoryMessage.Text = ex.Message; } }
    private async void HistoryRecover_Click(object sender, RoutedEventArgs e)
    {
        if (Maintenance is not { } history || historyUiBusy) return;
        historyUiBusy = true;
        try { await history.Recover(); HistoryMessage.Text = history.Status; }
        catch (Exception ex) { HistoryMessage.Text = ex.Message; }
        finally { historyUiBusy = false; await RefreshHistory(); }
    }
}
