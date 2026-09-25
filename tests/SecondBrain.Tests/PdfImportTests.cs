using SecondBrain.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class PdfImportTests
{
    private static byte[] Fixture(params string[] pages)
    {
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages) { var page = builder.AddPage(PageSize.A4); if (text.Length > 0) page.AddText(text, 12, new PdfPoint(25, 700), font); }
        return builder.Build();
    }
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("PDF imports preserve original bytes and retrieve distinct page references", () =>
        {
            var root = folder("pdf-vault"); var file = Path.Combine(folder("pdf-source"), "Release.pdf");
            var original = Fixture("Cobalt deployment uses a staged rollout.", "Topaz rollback needs review."); File.WriteAllBytes(file, original);
            var result = DocumentImports.Import(root, file, "Cedar"); check(result.State == "Imported", result.Message);
            check(File.ReadAllBytes(VaultFiles.SafePath(root, result.Document!.Original)).SequenceEqual(original), "PDF snapshot changed");
            var index = new VaultIndex(); index.Rebuild(root);
            var hit = index.Search("Topaz", new("Cedar"), new Dictionary<string, float[]>()).Hits.Single();
            check(hit.Chunk.Text.Contains("source page 2") && hit.Chunk.Text.Contains("#page=2"), "PDF page provenance lost");
            check(DocumentImports.Import(root, file, "Cedar").State == "Already imported", "PDF duplicate rewritten");
        });
        test("PDF extraction identifies textless pages without inventing content or hiding partial coverage", () =>
        {
            var result = PdfText.Read(Fixture("First page is searchable.", "", "Third page has evidence."));
            check(result.Passages.Any(p => p.Location.StartsWith("page 3,")) && result.Warnings.Any(w => w.Contains("pages 2;")), "Partial page coverage not disclosed");
            try { PdfText.Read(Fixture("")); throw new Exception("Blank PDF accepted"); }
            catch (InvalidDataException ex) { check(ex.Message.Contains("no extractable text") && ex.Message.Contains("OCR"), "Missing scanned/blank explanation"); }
        });
        test("Damaged, oversized, cancelled and over-page-limit PDFs cannot publish imports", () =>
        {
            var root = folder("pdf-failures"); var file = Path.Combine(folder("pdf-bad-source"), "Broken.pdf"); File.WriteAllText(file, "%PDF-1.7\nnot a document");
            var result = DocumentImports.Import(root, file, ""); check(result.State == "Failed" && result.Message.Contains("could not be read"), "Malformed PDF failure hidden");
            try { PdfText.Read(new byte[20_000_001]); throw new Exception("Oversized PDF accepted"); } catch (InvalidDataException) { }
            try { PdfText.Read(Fixture(Enumerable.Repeat("", 251).ToArray())); throw new Exception("Too many pages accepted"); } catch (InvalidDataException ex) { check(ex.Message.Contains("250"), "Wrong page limit failure"); }
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { PdfText.Read(Fixture("Cancelled."), stop.Token); throw new Exception("Cancelled PDF extracted"); } catch (OperationCanceledException) { }
            check(DocumentImports.List(root).Length == 0 && File.ReadAllText(file).EndsWith("not a document"), "Failure changed source or published partial import");
        });
    }
}
