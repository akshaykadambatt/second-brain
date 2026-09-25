using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace SecondBrain.Core;

public static class PdfText
{
    // Desktop callers isolate this parser in a bounded worker process.
    public static ImportExtraction Read(byte[] bytes, CancellationToken cancellation = default)
    {
        if (bytes.Length > 20_000_000) throw new InvalidDataException("PDF imports are limited to 20 MB.");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var document = PdfDocument.Open(bytes, ParsingOptions.LenientParsingOff);
            if (document.IsEncrypted) throw new InvalidDataException("Encrypted PDFs are not supported. Export an unencrypted copy first.");
            if (document.NumberOfPages is < 1 or > 250) throw new InvalidDataException("Choose a PDF with 1–250 pages, or split it before import.");
            var passages = new List<ImportPassage>(); var empty = new List<int>(); var characters = 0;
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                cancellation.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                var text = ContentOrderTextExtractor.GetText(page);
                if (string.IsNullOrWhiteSpace(text)) { empty.Add(number); continue; }
                characters += text.Length;
                if (characters > 500_000) throw new InvalidDataException("PDF text exceeds 500,000 characters. Split the document and retry.");
                foreach (var passage in DocumentImports.ExtractText(Encoding.UTF8.GetBytes(text)).Passages)
                    passages.Add(new($"page {number}, extracted {passage.Location}", passage.Text));
            }
            if (passages.Count == 0) throw new InvalidDataException("PDF has no extractable text. It may be scanned, image-only or blank; OCR is not included.");
            var warnings = new List<string> { "PDF reading order is estimated; verify tables and columns against the preserved original." };
            if (empty.Count > 0) warnings.Add("No text layer found on pages " + string.Join(", ", empty) + "; these may be scanned or blank. OCR is not included.");
            return new(passages.ToArray(), warnings.ToArray());
        }
        catch (PdfDocumentEncryptedException) { throw new InvalidDataException("Password-protected PDF could not be read. Export an unencrypted copy first."); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception) { throw new InvalidDataException("PDF could not be read. It may be damaged or use an unsupported structure. The source is unchanged."); }
    }
}
