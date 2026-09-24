using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ImportSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.KnowledgeTab.IsSelected = true;
        var source = Path.Combine(directory, "Synthetic brief.md"); var broken = Path.Combine(directory, "Broken.txt");
        File.WriteAllText(source, "# Synthetic launch\nThe cobalt release uses a staged pilot.\n"); File.WriteAllBytes(broken, [0, 255, 0]);
        var results = await main.ImportDocuments([source, broken], "Cedar");
        check(results.Length == 2 && results[0].State == "Imported" && results[1].State == "Failed", "A mixed import batch retains success and displays its separate failure");
        check(!main.Recorder.HasSession && main.Companion?.Active != true && !main.Visuals.Running, "Importing sources never starts audio or window capture");
        var knowledge = main.Knowledge!; await knowledge.Refresh(true);
        var found = await knowledge.Search("cobalt", CancellationToken.None);
        check(found.Hits.Any(h => h.Chunk.Project == "Cedar" && h.Chunk.Text.Contains("source lines")), "Imported text is available to production search with source locations");
        var item = results[0].Document!; var note = VaultFiles.SafePath(knowledge.Root, item.Note); File.AppendAllText(note, "\nManual note kept.\n");
        var duplicate = await main.ImportDocuments([source], "Cedar");
        check(duplicate.Single().State == "Already imported" && File.ReadAllText(note).Contains("Manual note kept"), "UI duplicate import preserves an edited searchable note");
        check(DocumentImports.List(knowledge.Root).Single().Document!.Sha256 == item.Sha256 && main.DocumentChoose.IsEnabled && !main.DocumentCancel.IsEnabled, "Saved imports reopen with controls restored after completion");
        main.DocumentImportResults.ItemsSource = results;
        capture(main, Path.Combine(directory, "imports.png"));
    }
}
