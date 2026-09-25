using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class OfficeImportSmokeTests
{
    internal static byte[] Package(params (string Path, string Text)[] parts)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var part in parts)
        { using var writer = new StreamWriter(zip.CreateEntry(part.Path).Open(), Encoding.UTF8); writer.Write(part.Text); }
        return bytes.ToArray();
    }
    internal static byte[] WordFixture() => Package(("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Cobalt Word rollout needs review.</w:t></w:r></w:p></w:body></w:document>"));
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.KnowledgeTab.IsSelected = true;
        var bytes = WordFixture(); var path = Path.Combine(directory, "Brief.docx"); File.WriteAllBytes(path, bytes);
        var result = (await main.ImportDocuments([path], "Cedar")).Single(); check(result.State == "Imported", "DOCX imports through the shared UI workflow");
        var root = main.Knowledge!.Root; await main.Knowledge.Refresh(true);
        var hits = await main.Knowledge.Search("Cobalt Word", CancellationToken.None);
        check(hits.Hits.Any(h => h.Chunk.Text.Contains("section 1, paragraph 1")), "Word search results retain section and paragraph provenance");
        check(File.ReadAllBytes(VaultFiles.SafePath(root, result.Document!.Original)).SequenceEqual(bytes), "The original DOCX is preserved exactly");
        check((await main.ImportDocuments([path], "Cedar")).Single().State == "Already imported", "Repeated Word import preserves the existing note");
        check(!main.Recorder.HasSession && main.DocumentChoose.IsEnabled, "Import does not start listening and restores controls");
        main.DocumentImportResults.ItemsSource = new[] { result }; capture(main, Path.Combine(directory, "word-import.png"));
    }
}
