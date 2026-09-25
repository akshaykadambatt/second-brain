using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ClientKnowledgeSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var client = new SessionContext { ProfileId = Guid.NewGuid(), Client = "Cedar" }; var root = main.Knowledge!.Root;
        File.WriteAllText(Path.Combine(root, "Source.md"), "Taylor will send the report Friday.");
        var window = new ClientKnowledgeWindow(root, [client], client.ProfileId, main.SaveClientKnowledge, _ => { }) { Owner = main }; window.Show();
        try
        {
            window.Kind.SelectedItem = KnowledgeKind.Commitment; window.NameField.Text = "Weekly report"; window.Aliases.Text = "rollout update"; window.Body.Text = "# Weekly report\nFollow up on the source commitment.";
            window.Source.Text = "Source.md"; window.Quote.Text = "Taylor will send the report Friday."; window.Date.Text = "2026-01-02"; window.OwnerField.Text = "Taylor"; window.Due.Text = "Friday";
            await window.Save(false); check(window.Status.Text.StartsWith("Saved observation"), window.Status.Text);
            await window.Save(true); check(new ClientKnowledgeStore(root).List(client.ProfileId).Documents.Single().Value.Status == "Confirmed", "Explicit UI confirmation persists");
            await window.Save(false); check(new ClientKnowledgeStore(root).List(client.ProfileId).Documents.Single().Value.Status == "Observation", "UI can reverse confirmation");
            check(!main.Recorder.HasSession, "Knowledge editing does not start capture"); capture(window, Path.Combine(directory, "client-knowledge.png"));
        }
        finally { window.Close(); }
    }
}
