using System.Text;
using SecondBrain.Core;

internal static class ImportTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Text imports preserve original bytes, searchable line provenance and project boundaries", () =>
        {
            var root = folder("import-vault"); var source = Path.Combine(folder("import-source"), "Brief.md");
            var original = Encoding.UTF8.GetBytes("# Aurora\r\nThe cobalt launch uses a staged rollout.\r\nA backup owner remains undecided.\r\n"); File.WriteAllBytes(source, original);
            var result = DocumentImports.Import(root, source, "Aurora"); check(result.State == "Imported", result.Message);
            var item = result.Document!;
            check(File.ReadAllBytes(VaultFiles.SafePath(root, item.Original)).SequenceEqual(original) && File.ReadAllBytes(source).SequenceEqual(original), "Original bytes changed");
            var index = new VaultIndex(); index.Rebuild(root);
            var hits = index.Search("cobalt", new("Aurora"), new Dictionary<string, float[]>()).Hits;
            check(hits.Count == 1 && hits[0].Chunk.Text.Contains("source lines 1–4") && hits[0].Chunk.Date is null, "Source lines or unknown fact date lost");
            check(index.Search("cobalt", new("Other"), new Dictionary<string, float[]>()).Hits.Count == 0, "Project filter leaked imported text");
            check(System.Text.Json.JsonSerializer.Serialize(DocumentImports.List(root).Single().Document) == System.Text.Json.JsonSerializer.Serialize(item), "Import metadata did not round trip");
        });
        test("Duplicate imports retain edits; changed originals and different projects remain separate", () =>
        {
            var root = folder("import-duplicates"); var source = Path.Combine(folder("import-revisions"), "Source.txt"); File.WriteAllText(source, "A grounded original.");
            var first = DocumentImports.Import(root, source, "Cedar").Document!; var note = VaultFiles.SafePath(root, first.Note); File.AppendAllText(note, "\nManual clarification.\n"); var edited = File.ReadAllBytes(note);
            var duplicate = DocumentImports.Import(root, source, "cedar"); check(duplicate.State == "Already imported" && File.ReadAllBytes(note).SequenceEqual(edited), duplicate.Message);
            var scoped = DocumentImports.Import(root, source, "Birch"); check(scoped.State == "Imported" && scoped.Document!.Id != first.Id, "Different project collapsed");
            File.WriteAllText(source, "A changed original."); var revised = DocumentImports.Import(root, source, "Cedar");
            check(revised.State == "Imported" && revised.Document!.Id != first.Id && File.ReadAllText(VaultFiles.SafePath(root, first.Original)) == "A grounded original.", "Source update overwrote prior version");
            File.WriteAllText(VaultFiles.SafePath(root, revised.Document!.Original), "Damaged snapshot");
            check(DocumentImports.Import(root, source, "Cedar").State == "Failed", "Damaged snapshot silently accepted");
        });
        test("Import failures, cancellation and vault lock never publish partial searchable notes", () =>
        {
            var root = folder("import-failures"); var source = Path.Combine(folder("import-invalid"), "Invalid.txt");
            foreach (var bytes in new[] { new byte[] { 0xff, 0, 1 }, Encoding.UTF8.GetBytes("\0binary"), Array.Empty<byte>(), new byte[2_000_001] })
            { File.WriteAllBytes(source, bytes); check(DocumentImports.Import(root, source, "").State == "Failed", "Unreadable source accepted"); }
            File.WriteAllText(source, "Valid retry.");
            using (var held = new VaultMaintenance(root).Acquire()) check(DocumentImports.Import(root, source, "").State == "Failed", "Busy vault was modified");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            check(DocumentImports.Import(root, source, "", cancel.Token).State == "Cancelled" && DocumentImports.List(root).Length == 0, "Cancelled work published");
            check(DocumentImports.Import(root, source, "").State == "Imported", "Retry did not recover");
        });
        test("UTF16 and long source lines retain traceable content without duplicate attachment indexing", () =>
        {
            var root = folder("import-unicode"); var source = Path.Combine(folder("import-utf16"), "Unicode.md");
            File.WriteAllText(source, "Résumé\n" + new string('x', 1400) + " terminalmarker", Encoding.Unicode);
            var result = DocumentImports.Import(root, source, ""); check(result.State == "Imported", result.Message);
            var index = new VaultIndex(); index.Rebuild(root);
            check(index.Chunks.All(c => !c.File.Contains("Attachments")), "Original was indexed a second time");
            var hits = index.Search("terminalmarker", new(), new Dictionary<string, float[]>());
            check(hits.Hits.Count == 1 && hits.Hits[0].Chunk.Text.Contains("source lines 2–2"), "Long line reference lost");
            var metadata = VaultFiles.SafePath(root, "Imports/" + result.Document!.Id + "/import.json"); File.WriteAllText(metadata, "{}");
            check(DocumentImports.List(root).Single().State == "Unreadable import", "Invalid manifest hid failure");
        });
    }
}
