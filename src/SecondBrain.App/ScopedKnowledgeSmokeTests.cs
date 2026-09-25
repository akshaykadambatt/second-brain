using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ScopedKnowledgeSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.ContextClient.Text = "Cedar"; var client = main.SaveMeetingContext(); main.KnowledgeClient.SelectedItem = client;
        var root = main.Knowledge!.Root;
        File.WriteAllText(Path.Combine(root, "client.md"), $"---\nclient_id: {client.ProfileId}\ndate: 2026-01-02\n---\n# 00:12:30 · System\nLaunch deadline is Friday.");
        File.WriteAllText(Path.Combine(root, "other.md"), $"---\nclient_id: {Guid.NewGuid()}\n---\n# Launch\nFOREIGN_SECRET deadline is Monday.");
        await main.Knowledge.Refresh(true); main.VaultQuery.Text = "launch deadline"; await main.SearchClientKnowledge(true);
        check(main.KnowledgeAnswer.Text.Contains("Friday") && main.KnowledgeAnswer.Text.Contains("00:12:30") && !main.KnowledgeAnswer.Text.Contains("FOREIGN_SECRET"), "Client answer cites timestamped passages with no foreign material");
        check(main.KnowledgeAnswer.Text.Contains("client.md:") && main.KnowledgeAnswer.Text.Contains("[S1]"), "Answer has a traceable passage citation");
        main.VaultQuery.Text = "unanswerableterm"; await main.SearchClientKnowledge(true); check(main.KnowledgeAnswer.Text.Contains("don’t have evidence"), "Missing evidence produces an explicit abstention");
        var source = Path.Combine(directory, "Scoped.txt"); File.WriteAllText(source, "Cobalt planning note.");
        var imported = (await main.ImportDocuments([source], "", client.ProfileId)).Single(); check(imported.Document?.ClientId == client.ProfileId, "Import coordinator preserves selected client scope");
        check(!main.Recorder.HasSession, "Knowledge answers do not start capture"); main.KnowledgeTab.IsSelected = true; capture(main, Path.Combine(directory, "scoped-answer.png"));
    }
}
