using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private Task storageTask = Task.CompletedTask;
    private bool storageBusy;
    private string? storageResult;
    internal async Task RefreshStorage()
    {
        if (Knowledge is not { } knowledge) return;
        try
        {
            var sizes = await Task.Run(() => LocalBackup.Measure(dataDirectory, knowledge.Root));
            StorageUsage.Text = string.Join("\n", sizes.Select(s => $"{s.Category}: {s.Bytes / 1048576d:N1} MB · {s.Files:N0} files")) + $"\nTotal: {sizes.Sum(s => s.Bytes) / 1048576d:N1} MB";
            StorageRecordings.ItemsSource = LocalBackup.Recordings(dataDirectory).Select(p => new StoredRecording(p)).ToArray();
        }
        catch (Exception ex) { StorageMessage.Text = "Storage scan could not finish: " + ex.Message; }
    }
    private sealed record StoredRecording(string Path) { public override string ToString() => System.IO.Path.GetFileName(Path); }
    private async void StorageRefresh_Click(object sender, RoutedEventArgs e) => await RefreshStorage();
    internal Task<string?> StorageOperation(Func<string, string> operation)
    {
        if (storageBusy || closing || companionBusy || Companion?.Active == true || Recorder.HasSession || Recorder.Busy || Voice.Running || startingVoice)
        { StorageMessage.Text = "Stop listening or recording before using backup, restore or deletion."; return Task.FromResult<string?>(null); }
        storageBusy = true;
        var task = Run(); storageTask = task; return task;
        async Task<string?> Run()
        {
            WorkspaceGrid.IsEnabled = false; contextTimer.Stop(); Playback.Pause();
            var root = Knowledge?.Root;
            try
            {
                if (root is null) throw new IOException("Connect a vault first.");
                StorageMessage.Text = "Preparing local files… Pending note updates will resume afterward.";
                Assistant?.Close(); StreamDemo?.Close(); study?.Close(); replay?.Stop();
                if (recordingWindow is { } capture) { await capture.RecoveryTask; capture.Close(); }
                await Task.WhenAll(assistantShutdowns);
                await StopKnowledge(); Knowledge = null;
                saveTimer.Stop(); if (!SaveSettings()) throw new IOException("Current settings could not be saved. Check folder access and retry.");
                StorageMessage.Text = "Working on local files… Large recordings can take a few minutes.";
                var result = await Task.Run(() => operation(root));
                storageResult = Directory.Exists(result) ? result : null;
                StorageMessage.Text = result; return result;
            }
            catch (Exception ex) { StorageMessage.Text = "Operation did not finish: " + ex.Message + " Existing backups and the current installation were not replaced. A .partial folder may remain; it is not a completed backup. If deletion failed, some selected recording files may already be removed."; return null; }
            finally
            {
                storageBusy = false; WorkspaceGrid.IsEnabled = true;
                if (!closing)
                {
                    try { if (root is not null) OpenKnowledge(root, vaultOptions); } catch (Exception ex) { StorageMessage.Text += " Reconnect the vault: " + ex.Message; }
                    contextTimer.Start(); await RefreshStorage();
                }
            }
        }
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose where to save a new verified backup folder" };
        if (picker.ShowDialog(this) != true) return;
        var result = await StorageOperation(root => LocalBackup.Create(dataDirectory, root, Environment.ProcessPath!, picker.FolderName));
        if (result is not null) StorageMessage.Text = "Verified backup saved to:\n" + result + "\nKeep this folder unchanged. Restore it into a new app folder using Restore backup.";
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var source = new OpenFolderDialog { Title = "Choose the completed SecondBrain-backup folder" };
        if (source.ShowDialog(this) != true) return;
        var target = new OpenFolderDialog { Title = "Choose where to create a separate restored app folder" };
        if (target.ShowDialog(this) != true) return;
        var result = await StorageOperation(_ => LocalBackup.Restore(source.FolderName, target.FolderName));
        if (result is not null) StorageMessage.Text = "Verified and restored to:\n" + result + "\nClose this app, then run SecondBrain.exe in that folder. Its data and Vault are alongside it; this installation stays unchanged.";
    }
    private async void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        if (StorageRecordings.SelectedItem is not StoredRecording recording) { StorageMessage.Text = "Select a saved recording first."; return; }
        if (MessageBox.Show(this, "Permanently delete this recording's audio, chunks and local transcripts?\n\n" + recording + "\n\nExported vault transcripts, notes, Git history and backups remain. Audio links in the vault will no longer open. This action cannot be undone here.", "Delete selected recording", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await StorageOperation(_ => { LocalBackup.DeleteRecording(dataDirectory, recording.Path); return "Selected recording deleted. Vault copies, history and backups are retained."; });
    }
    private async void StorageRecover_Click(object sender, RoutedEventArgs e)
    {
        if (StorageRecordings.SelectedItem is not StoredRecording recording) { StorageMessage.Text = "Select an interrupted recording first."; return; }
        await StorageOperation(_ => { LocalBackup.RecoverRecording(dataDirectory, recording.Path); return "Available audio and transcript recovered. Use Knowledge → Import saved meetings to add this session to the vault."; });
    }
    private void StorageOpen_Click(object sender, RoutedEventArgs e)
    {
        if (storageResult is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "\"" + storageResult + "\"" }); }
        catch (Exception ex) { StorageMessage.Text = ex.Message; }
    }
}
