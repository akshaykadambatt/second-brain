using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    internal KnowledgeService? Knowledge { get; private set; }
    private VaultSettings? vaultSettings;
    private VaultOptions vaultOptions = new();
    private Task vaultImport = Task.CompletedTask;
    private readonly List<Task> knowledgeStops = [];
    private Guid sourcesRequest;
    private KnowledgeResult? sourcesSnapshot;
    private string? companionVaultRoot;
    private void InitializeKnowledge()
    {
        // Smoke-test vaults stay under their isolated data root. Normal default
        // is beside the EXE so the executable, Vault and data can move together.
        var home = hiddenTestMode ? dataDirectory : AppContext.BaseDirectory;
        vaultSettings = new(dataDirectory, home);
        try { vaultOptions = vaultSettings.Load(); } catch (Exception) { VaultStatus.Text = "Invalid vault settings; using the adjacent Vault folder."; }
        VaultFolder.Text = vaultSettings.Resolve(vaultOptions); SemanticCheck.IsChecked = vaultOptions.Semantic;
        AutoUpdates.IsChecked = vaultOptions.AutomaticUpdates;
        VaultProject.Text = vaultOptions.Project; VaultFrom.Text = vaultOptions.From?.ToString("yyyy-MM-dd") ?? ""; VaultUntil.Text = vaultOptions.Until?.ToString("yyyy-MM-dd") ?? "";
        try { ConnectKnowledge(); } catch (Exception ex) { VaultStatus.Text = "Vault unavailable: " + ex.Message; }
    }
    private void ConnectKnowledge()
    {
        var root = Path.GetFullPath(VaultFolder.Text); VaultFiles.Initialize(root);
        DateOnly? Date(string value) => string.IsNullOrWhiteSpace(value) ? null : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", out var date) ? date : throw new InvalidOperationException("Use YYYY-MM-DD dates or leave dates empty.");
        var options = new VaultOptions(vaultSettings!.Portable(root), SemanticCheck.IsChecked == true, VaultProject.Text.Trim(), Date(VaultFrom.Text), Date(VaultUntil.Text), AutoUpdates.IsChecked == true);
        vaultSettings.Save(options); vaultOptions = options;
        OpenKnowledge(root, options);
    }
    private void OpenKnowledge(string root, VaultOptions options)
    {
        if (Knowledge is { } previous)
        {
            async Task Finish() { await previous.Stop(); previous.Dispose(); }
            knowledgeStops.RemoveAll(t => t.IsCompleted); knowledgeStops.Add(Finish());
        }
        Knowledge = new(root, dataDirectory, new(options.Project, options.From, options.Until), !hiddenTestMode && options.Semantic ? new OpenAiEmbeddings(new ApiKeyStore(dataDirectory, "OpenAI").Load) : null);
        VaultResults.Items.Clear();
        _ = Knowledge.Refresh(true); VaultStatus.Text = "Vault connected · indexing Markdown…";
        historyConnection = ConnectMaintenance(root, options.AutomaticUpdates);
    }
    private void TickKnowledge()
    {
        if (Knowledge is null || closing) return;
        _ = Knowledge.Refresh(); VaultStatus.Text = Knowledge.Status;
        VaultApply.IsEnabled = VaultChoose.IsEnabled = !companionBusy && Companion?.Active != true && vaultImport.IsCompleted && historyConnection.IsCompleted && Maintenance?.Busy != true;
        VaultImport.IsEnabled = !Recorder.HasSession && !Recorder.Busy && vaultImport.IsCompleted;
        TickMaintenance();
    }
    private void VaultApply_Click(object sender, RoutedEventArgs e)
    {
        if (Companion?.Active == true || !vaultImport.IsCompleted || !historyConnection.IsCompleted || Maintenance?.Busy == true) return;
        try { ConnectKnowledge(); } catch (Exception ex) { VaultSearchStatus.Text = "Could not connect vault: " + ex.Message; }
    }
    private void VaultChoose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose or create your Markdown vault folder" };
        if (picker.ShowDialog(this) != true) return;
        VaultFolder.Text = picker.FolderName; VaultApply_Click(sender, e);
    }
    private async void VaultRebuild_Click(object sender, RoutedEventArgs e)
    {
        try { if (Knowledge is { } knowledge) await knowledge.Rebuild(); }
        catch (Exception) { VaultSearchStatus.Text = "Rebuild could not finish; Markdown source notes are unchanged."; }
    }
    private void VaultExplorer_Click(object sender, RoutedEventArgs e)
    {
        try { if (Knowledge is { } knowledge) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "\"" + knowledge.Root + "\"" }); }
        catch (Exception) { VaultSearchStatus.Text = "Could not open the vault folder."; }
    }
    private void VaultObsidian_Click(object sender, RoutedEventArgs e)
    { if (Knowledge is { } knowledge) OpenVaultNote(knowledge.Root, "Home.md", true); }
    private void OpenVaultNote(string root, string relative, bool obsidian = false)
    {
        try
        {
            var path = VaultFiles.SafePath(root, relative);
            Process.Start(new ProcessStartInfo(obsidian ? "obsidian://open?path=" + Uri.EscapeDataString(path) : path) { UseShellExecute = true });
        }
        catch (Exception) { VaultSearchStatus.Text = "Open Obsidian once, choose Open folder as vault, and select the displayed Vault folder. Or use Open folder to edit the notes."; }
    }
    private async void VaultSearch_Click(object sender, RoutedEventArgs e)
    {
        if (Knowledge is not { } knowledge || string.IsNullOrWhiteSpace(VaultQuery.Text)) return;
        VaultSearch.IsEnabled = false;
        try
        {
            var result = await knowledge.Search(VaultQuery.Text, CancellationToken.None);
            if (Knowledge != knowledge || closing) return;
            VaultResults.Items.Clear();
            foreach (var hit in result.Hits) VaultResults.Items.Add(SourceRow(hit));
            VaultSearchStatus.Text = result.Status;
        }
        catch (Exception) { VaultSearchStatus.Text = "Search unavailable; reconnect or rebuild the vault."; }
        finally { VaultSearch.IsEnabled = true; }
    }
    private static ListBoxItem SourceRow(KnowledgeHit hit) => new() { Tag = hit, Content = new TextBlock { Text = $"{hit.Chunk.File}:{hit.Chunk.Line} · {hit.Chunk.Date?.ToString("yyyy-MM-dd") ?? "undated"}\n{hit.Chunk.Text}", TextWrapping = TextWrapping.Wrap, MaxHeight = 140 }, Padding = new Thickness(6) };
    private void VaultOpenResult_Click(object sender, RoutedEventArgs e)
    { if (Knowledge is { } knowledge && VaultResults.SelectedItem is ListBoxItem { Tag: KnowledgeHit hit }) OpenVaultNote(knowledge.Root, hit.Chunk.File, true); }
    private void SourceOpen_Click(object sender, RoutedEventArgs e)
    { if (companionVaultRoot is { } root && LiveSources.SelectedItem is ListBoxItem { Tag: KnowledgeHit hit }) OpenVaultNote(root, hit.Chunk.File, true); }
    private void RefreshSources()
    {
        var selected = Companion?.Selected;
        var request = Companion?.Answers.Requests.FirstOrDefault(r => r.Id == selected?.RequestId);
        if (request?.Knowledge is not { } result || sourcesRequest == request.Id && ReferenceEquals(sourcesSnapshot, result)) return;
        sourcesRequest = request.Id; sourcesSnapshot = result; LiveSources.Items.Clear();
        foreach (var hit in result.Hits) LiveSources.Items.Add(SourceRow(hit));
        SourceStatus.Text = result.Status + " · retrieved snapshots, not individually verified citations";
    }
    private async Task ExportCurrentMeeting()
    {
        if (Knowledge is not { } knowledge || Recorder.LastDirectory is not { } directory || !File.Exists(Path.Combine(directory, "transcript.jsonl"))) return;
        try
        {
            var summary = await Task.Run(() => VaultFiles.ExportMeeting(knowledge.Root, directory, vaultOptions.Project));
            await historyConnection; Maintenance?.Queue(summary);
            _ = knowledge.Refresh(true);
        }
        catch (Exception ex) { CompanionStatus.Text += " Vault export could not finish; original recording is safe. Use Import saved meetings to retry."; log.Write("Vault export failure=" + ex.GetType().Name); }
    }
    private async void VaultImport_Click(object sender, RoutedEventArgs e)
    {
        if (Knowledge is not { } knowledge || Recorder.HasSession || !vaultImport.IsCompleted) return;
        var recordings = Path.Combine(dataDirectory, "recordings"); var failed = 0; var imported = 0;
        vaultImport = Task.Run(() =>
        {
            if (!Directory.Exists(recordings)) return;
            foreach (var folder in Directory.EnumerateDirectories(recordings))
            {
                if (!File.Exists(Path.Combine(folder, "transcript.jsonl"))) continue;
                try { VaultFiles.ExportMeeting(knowledge.Root, folder); imported++; }
                catch (Exception) { failed++; }
            }
        });
        TickKnowledge();
        try
        {
            await vaultImport; await knowledge.Refresh(true);
            VaultSearchStatus.Text = $"Imported/preserved {imported} sessions; {failed} need stop/recovery or readable source files.";
            Maintenance?.QueueSavedMeetings();
        }
        catch (Exception) { VaultSearchStatus.Text = "Could not read saved recordings. Original files are unchanged; check folder access and retry."; }
    }
    private async Task StopKnowledge()
    {
        try { await vaultImport; } catch (Exception) { /* Import already reported its failure; permit clean shutdown. */ }
        await historyConnection;
        if (Maintenance is { } maintenance) { await maintenance.Stop(); maintenance.Dispose(); Maintenance = null; }
        if (Knowledge is { } knowledge) { await knowledge.Stop(); knowledge.Dispose(); }
        await Task.WhenAll(knowledgeStops);
    }
}
