using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private Guid KnowledgeClientId => (KnowledgeClient.SelectedItem as SessionContext)?.ProfileId ?? Guid.Empty;
    private void KnowledgeClient_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        VaultResults.Items.Clear(); KnowledgeAnswer.Text = "";
        VaultSearchStatus.Text = "Scope changed. Search again to retrieve this client’s sources.";
    }
    private async void KnowledgeAnswer_Click(object sender, RoutedEventArgs e) => await SearchClientKnowledge(true);
    internal async Task SearchClientKnowledge(bool answer)
    {
        if (Knowledge is not { } knowledge || string.IsNullOrWhiteSpace(VaultQuery.Text)) return;
        var client = KnowledgeClientId; VaultSearch.IsEnabled = false;
        try
        {
            var result = await knowledge.ForClient(client).Search(VaultQuery.Text, CancellationToken.None);
            if (Knowledge != knowledge || closing || client != KnowledgeClientId) return;
            VaultResults.Items.Clear(); foreach (var hit in result.Hits) VaultResults.Items.Add(SourceRow(hit));
            VaultSearchStatus.Text = result.Status;
            if (answer) KnowledgeAnswer.Text = ScopedKnowledge.Answer(result);
        }
        catch (Exception ex) { VaultSearchStatus.Text = "Search unavailable: " + ex.Message; }
        finally { VaultSearch.IsEnabled = true; }
    }
    private async void AssignSource_Click(object sender, RoutedEventArgs e)
    {
        if (Knowledge is not { } knowledge || !vaultImport.IsCompleted || storageBusy || closing) return;
        var picker = new OpenFileDialog { Title = "Assign a vault note to the selected client", InitialDirectory = knowledge.Root, Filter = "Vault Markdown|*.md", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var relative = Path.GetRelativePath(knowledge.Root, picker.FileName).Replace('\\', '/'); var path = VaultFiles.SafePath(knowledge.Root, relative);
            if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Source note exceeds 2 MB.");
            var expected = VaultIndex.Hash(File.ReadAllText(path)); var client = KnowledgeClientId;
            async Task Assign()
            {
                await historyConnection;
                if (Maintenance is { } maintenance) await maintenance.AssignClientSource(relative, client, expected);
                else await Task.Run(() => ScopedKnowledge.Assign(knowledge.Root, relative, client, expected));
                await knowledge.Refresh(true);
            }
            vaultImport = Assign(); await vaultImport;
            VaultSearchStatus.Text = "Note assigned. Other source notes keep their current scope. Repeat with General to unassign.";
            VaultResults.Items.Clear(); KnowledgeAnswer.Clear();
        }
        catch (Exception ex) { VaultSearchStatus.Text = "Assignment failed: " + ex.Message; }
    }
}
