using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private void ClientKnowledge_Click(object sender, RoutedEventArgs e)
    {
        if (Knowledge is not { } knowledge) return;
        new ClientKnowledgeWindow(knowledge.Root, contextBook.Profiles, contextBook.SelectedProfileId, SaveClientKnowledge,
            relative => OpenVaultNote(knowledge.Root, relative, true), (VaultResults.SelectedItem as ListBoxItem)?.Tag as KnowledgeHit) { Owner = this }.ShowDialog();
    }
    internal Task<KnowledgeDocument> SaveClientKnowledge(ClientKnowledge value, string? revision)
    {
        if (closing || storageBusy || Knowledge is not { } knowledge || !vaultImport.IsCompleted) throw new InvalidOperationException("Wait for the current vault operation before saving.");
        async Task<KnowledgeDocument> Run()
        {
            await historyConnection;
            var saved = Maintenance is { } maintenance ? await maintenance.SaveClientKnowledge(value, revision)
                : await Task.Run(() => new ClientKnowledgeStore(knowledge.Root).Save(value, revision));
            await knowledge.Refresh(true); return saved;
        }
        var task = Run(); vaultImport = task; return task;
    }
}
