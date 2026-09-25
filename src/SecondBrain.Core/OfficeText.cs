using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SecondBrain.Core;

// Read-only OOXML extraction. Never launches Office, evaluates fields, or follows external links.
public static class OfficeText
{
    internal sealed class Package : IDisposable
    {
        private readonly ZipArchive archive;
        private readonly CancellationToken cancellation;
        public Package(byte[] bytes, CancellationToken cancellation)
        {
            this.cancellation = cancellation; cancellation.ThrowIfCancellationRequested();
            if (bytes.Length > 20_000_000) throw new InvalidDataException("Office files are limited to 20 MB.");
            archive = new(new MemoryStream(bytes, false), ZipArchiveMode.Read);
            try
            {
                long size = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellation.ThrowIfCancellationRequested(); size += entry.Length;
                    if (archive.Entries.Count > 4096 || size > 64_000_000 || !names.Add(entry.FullName)
                        || entry.FullName.Split('/').Any(p => p is ".." or ".") || entry.FullName.Contains('\\'))
                        throw new InvalidDataException("Office package exceeds limits or contains ambiguous paths.");
                }
            }
            catch { archive.Dispose(); throw; }
        }
        public XDocument Xml(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(path) ?? throw new InvalidDataException("Office document part is missing: " + path);
            if (entry.Length > 8_000_000) throw new InvalidDataException("Office XML part exceeds the 8 MB limit.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            return XDocument.Load(reader);
        }
        public void Dispose() => archive.Dispose();
    }
    public static ImportExtraction Word(byte[] bytes, CancellationToken cancellation = default)
    {
        try
        {
            using var package = new Package(bytes, cancellation); var document = package.Xml("word/document.xml");
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;
            if (ns.NamespaceName is not ("http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main")
                || document.Root?.Name.LocalName != "document") throw new InvalidDataException("Not a supported Word document.");
            var body = document.Root.Element(ns + "body") ?? throw new InvalidDataException("Word document body is missing.");
            var passages = new List<ImportPassage>(); var section = 1; var paragraph = 0; var count = 0;
            foreach (var p in body.Descendants(ns + "p"))
            {
                cancellation.ThrowIfCancellationRequested(); paragraph++;
                var text = new StringBuilder();
                foreach (var node in p.Descendants().Where(n => n.Ancestors(ns + "p").FirstOrDefault() == p && !n.Ancestors(ns + "del").Any()))
                {
                    if (node.Name == ns + "t") text.Append(node.Value);
                    else if (node.Name == ns + "tab") text.Append('\t');
                    else if (node.Name == ns + "br" || node.Name == ns + "cr") text.Append('\n');
                }
                var value = text.ToString(); count += value.Length;
                if (count > 500_000 || paragraph > 20_000) throw new InvalidDataException("Word text exceeds 500,000 characters or 20,000 paragraphs. Split the document.");
                if (!string.IsNullOrWhiteSpace(value)) foreach (var part in DocumentImports.ExtractText(Encoding.UTF8.GetBytes(value)).Passages)
                    passages.Add(new($"section {section}, paragraph {paragraph}, extracted {part.Location}", part.Text));
                if (p.Element(ns + "pPr")?.Element(ns + "sectPr") is not null) section++;
            }
            if (passages.Count == 0) throw new InvalidDataException("Word document has no extractable body text. Image-only documents need OCR first.");
            return new(passages.ToArray(), ["Word body paragraphs and tables extracted. Headers, footnotes, comments, images and field instructions are not included; verify the preserved original. Section and paragraph numbers follow document order."]);
        }
        catch (XmlException) { throw new InvalidDataException("Word XML is malformed or contains unsupported external entities. The source is unchanged."); }
        catch (InvalidDataException ex) { throw new InvalidDataException("DOCX could not be imported: " + ex.Message + " Encrypted files must be exported as unencrypted DOCX."); }
    }
}
