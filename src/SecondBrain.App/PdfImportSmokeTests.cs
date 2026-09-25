using System.Diagnostics;
using System.IO;
using System.Windows;
using SecondBrain.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SecondBrain.App;

internal static class PdfImportSmokeTests
{
    internal static byte[] Fixture(params string[] pages)
    {
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages) { var page = builder.AddPage(PageSize.A4); if (text.Length > 0) page.AddText(text, 12, new PdfPoint(25, 700), font); }
        return builder.Build();
    }
    private static bool Exited(int id) { try { using var process = Process.GetProcessById(id); return process.HasExited; } catch (ArgumentException) { return true; } }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.KnowledgeTab.IsSelected = true;
        var original = Fixture("Cobalt launch uses a staged rollout.", "", "Topaz rollback requires review.");
        var source = Path.Combine(directory, "Source.pdf"); File.WriteAllBytes(source, original);
        var broken = Path.Combine(directory, "Broken.pdf"); File.WriteAllText(broken, "%PDF-1.7\nbroken");
        var results = await main.ImportDocuments([source, broken], "Cedar");
        check(results.Length == 2 && results[0].State == "Imported" && results[1].State == "Failed", "Packaged PDF worker imports readable content and isolates malformed-file failure");
        check(results[0].Message.Contains("pages 2;") && results[0].Message.Contains("OCR"), "Mixed PDF explicitly identifies the page without a text layer");
        var saved = results[0].Document!; var knowledge = main.Knowledge!; await knowledge.Refresh(true);
        var hits = await knowledge.Search("Topaz", CancellationToken.None);
        check(hits.Hits.Any(h => h.Chunk.Text.Contains("source page 3") && h.Chunk.Text.Contains("#page=3")), "Production retrieval retains PDF page references and original-page links");
        check(File.ReadAllBytes(VaultFiles.SafePath(knowledge.Root, saved.Original)).SequenceEqual(original), "PDF original bytes survive extraction unchanged");
        var imageOnly = new PdfDocumentBuilder();
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4)));
        using var pixels = new MemoryStream(); png.Save(pixels);
        imageOnly.AddPage(PageSize.A4).AddPng(pixels.ToArray(), new PdfRectangle(20, 20, 200, 200));
        var scanned = Path.Combine(directory, "Image only.pdf"); File.WriteAllBytes(scanned, imageOnly.Build());
        var unavailable = (await main.ImportDocuments([scanned], "Cedar")).Single();
        check(unavailable.State == "Failed" && unavailable.Message.Contains("no extractable text"), "Image-only PDF reports unavailable text instead of inventing searchable content");
        var before = Directory.GetDirectories(Path.GetTempPath(), "SecondBrain-pdf-*").ToHashSet(); var timedOut = false; var timeoutPid = 0;
        try { await Task.Run(() => PdfImportWorker.ExtractBounded(original, CancellationToken.None, TimeSpan.Zero, id => timeoutPid = id)); }
        catch (IOException ex) { timedOut = ex.Message.Contains("timed out"); }
        check(timedOut && timeoutPid != 0 && Exited(timeoutPid), "Timed-out PDF worker exits without leaving a child process");
        using var stop = new CancellationTokenSource(); var cancelled = false; var cancelPid = 0;
        try { await Task.Run(() => PdfImportWorker.ExtractBounded(original, stop.Token, TimeSpan.FromSeconds(30), id => { cancelPid = id; stop.Cancel(); })); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && cancelPid != 0 && Exited(cancelPid), "Cancellation after worker launch terminates the child process");
        check(Directory.GetDirectories(Path.GetTempPath(), "SecondBrain-pdf-*").All(before.Contains), "Timeout and cancellation clean their temporary source and result files");
        check(!main.Recorder.HasSession && !main.Visuals.Running && main.DocumentChoose.IsEnabled, "PDF operations leave capture idle and the UI usable");
        var notices = MainWindow.PdfNotices(); check(notices.Contains("Apache License") && notices.Contains("Adobe Glyph List"), "The single EXE includes the PDF library license and upstream notices");
        main.DocumentImportResults.ItemsSource = results; main.DocumentImportStatus.Text = "PDF fixture passed: page references, partial coverage, isolated failures, timeout and cancellation.";
        capture(main, Path.Combine(directory, "pdf-imports.png"));
    }
}
