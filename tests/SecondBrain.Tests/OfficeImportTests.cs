using System.IO.Compression;
using System.Text;
using SecondBrain.Core;

internal static class OfficeImportTests
{
    internal static byte[] Package(params (string Path, string Text)[] parts)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var part in parts)
        { using var writer = new StreamWriter(zip.CreateEntry(part.Path).Open(), Encoding.UTF8); writer.Write(part.Text); }
        return bytes.ToArray();
    }
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("PPTX follows relationship order and preserves hidden and blank slide provenance", () =>
        {
            var bytes = PresentationFixture(); var result = OfficeText.Presentation(bytes);
            check(result.Passages[0].Location.StartsWith("slide 1 (hidden)") && result.Passages[0].Text.Contains("Cobalt"), "Slide ordering or hidden provenance lost");
            check(result.Passages[1].Location.StartsWith("slide 2") && result.Warnings.Any(w => w.EndsWith("3")), "Blank slide or second slide reference lost");
            var root = folder("slides-vault"); var source = Path.Combine(folder("slides-source"), "Deck.pptx"); File.WriteAllBytes(source, bytes);
            var imported = DocumentImports.Import(root, source, "Cedar"); check(imported.State == "Imported", imported.Message);
            check(File.ReadAllBytes(VaultFiles.SafePath(root, imported.Document!.Original)).SequenceEqual(bytes), "Presentation original changed");
            check(DocumentImports.Import(root, source, "Cedar").State == "Already imported", "Presentation duplicate not detected");
        });
        test("PPTX rejects external, malformed and missing slide references", () =>
        {
            foreach (var bytes in new[] { PresentationFixture(" TargetMode='External'"), Package(("ppt/presentation.xml", "<x/>")), Encoding.UTF8.GetBytes("invalid") })
            { try { OfficeText.Presentation(bytes); throw new Exception("Invalid presentation accepted"); } catch (InvalidDataException) { } }
        });
        test("DOCX body extraction retains sections, paragraphs and table text with original bytes", () =>
        {
            var xml = "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:pPr><w:sectPr/></w:pPr><w:r><w:t>First section.</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Cobalt</w:t><w:tab/><w:t>rollout</w:t></w:r><w:del><w:r><w:t>obsolete</w:t></w:r></w:del></w:p></w:tc></w:tr></w:tbl></w:body></w:document>";
            var bytes = Package(("word/document.xml", xml)); var root = folder("word-vault"); var source = Path.Combine(folder("word-source"), "Brief.docx"); File.WriteAllBytes(source, bytes);
            var result = DocumentImports.Import(root, source, "Cedar"); check(result.State == "Imported", result.Message);
            var index = new VaultIndex(); index.Rebuild(root); var hit = index.Search("Cobalt", new("Cedar"), new Dictionary<string, float[]>()).Hits.Single();
            check(hit.Chunk.Text.Contains("section 2, paragraph 2") && !hit.Chunk.Text.Contains("obsolete"), "Section provenance or deleted-text exclusion failed");
            check(File.ReadAllBytes(VaultFiles.SafePath(root, result.Document!.Original)).SequenceEqual(bytes), "Original DOCX changed");
            check(DocumentImports.Import(root, source, "Cedar").State == "Already imported", "DOCX duplicate changed existing import");
        });
        test("DOCX rejects unreadable, empty, duplicate and entity-bearing packages safely", () =>
        {
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes("not a zip"), Package(("word/document.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///unused'>]><x>&e;</x>")), Package(("word/document.xml", "<x/>")), Package(("word/document.xml", "<x/>"), ("word/document.xml", "<x/>")) })
            { try { OfficeText.Word(bytes); throw new Exception("Unsupported package accepted"); } catch (InvalidDataException) { } }
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { OfficeText.Word([], stop.Token); throw new Exception("Cancelled extraction ran"); } catch (OperationCanceledException) { }
        });
    }
    internal static byte[] PresentationFixture(string mode = "") => Package(
        ("ppt/presentation.xml", "<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><p:sldIdLst><p:sldId r:id='first'/><p:sldId r:id='second'/><p:sldId r:id='blank'/></p:sldIdLst></p:presentation>"),
        ("ppt/_rels/presentation.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='first' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide9.xml'{mode}/><Relationship Id='second' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide1.xml'/><Relationship Id='blank' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide3.xml'/></Relationships>"),
        ("ppt/slides/slide9.xml", "<p:sld show='0' xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>Cobalt rollout</a:t></a:r></a:p></p:sld>"),
        ("ppt/slides/slide1.xml", "<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>Second slide</a:t></a:r></a:p></p:sld>"),
        ("ppt/slides/slide3.xml", "<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main'/>"));
}
