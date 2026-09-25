using System.IO;
using System.Windows;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private CancellationTokenSource? documentImportCancellation;
    private async void DocumentChoose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import source documents", Filter = "Supported documents|*.md;*.txt;*.pdf;*.docx;*.pptx|Markdown and text|*.md;*.txt|PDF documents|*.pdf|Word documents|*.docx|PowerPoint presentations|*.pptx", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await ImportDocuments(picker.FileNames, ImportProject.Text);
    }
    internal Task<ImportResult[]> ImportDocuments(string[] paths, string project)
    {
        if (closing || storageBusy || Knowledge is null || !vaultImport.IsCompleted)
        { DocumentImportStatus.Text = "Wait for the current operation and connect a vault before importing."; return Task.FromResult(Array.Empty<ImportResult>()); }
        if (paths.Length is < 1 or > 20)
        { DocumentImportStatus.Text = "Choose between 1 and 20 files per batch."; return Task.FromResult(Array.Empty<ImportResult>()); }
        documentImportCancellation?.Dispose(); documentImportCancellation = new();
        var token = documentImportCancellation.Token; var knowledge = Knowledge;
        var task = Run(); vaultImport = task; return task;
        async Task<ImportResult[]> Run()
        {
            DocumentImportStatus.Text = "Importing locally… waiting for any current note update. Cancel is available.";
            DocumentChoose.IsEnabled = false; DocumentCancel.IsEnabled = true;
            try
            {
                await historyConnection;
                var results = Maintenance is { } maintenance ? await maintenance.ImportDocuments(paths, project, token)
                    : await Task.Run(() => paths.Select(path => DocumentImports.Import(knowledge.Root, path, project, token, PdfImportWorker.Extract)).ToArray(), token);
                if (!closing && Knowledge == knowledge)
                {
                    DocumentImportResults.ItemsSource = results;
                    await knowledge.Refresh(true);
                    DocumentImportStatus.Text = $"{results.Count(r => r.State == "Imported")} imported · {results.Count(r => r.State == "Already imported")} duplicates · {results.Count(r => r.State is "Failed" or "Cancelled")} need attention. Select a result for its source or note.";
                }
                return results;
            }
            catch (OperationCanceledException) { DocumentImportStatus.Text = "Import cancelled. Completed files remain available in Saved imports."; return []; }
            catch (Exception ex) { DocumentImportStatus.Text = "Import could not finish: " + ex.Message; return []; }
            finally { DocumentChoose.IsEnabled = true; DocumentCancel.IsEnabled = false; }
        }
    }
    private void DocumentCancel_Click(object sender, RoutedEventArgs e) => documentImportCancellation?.Cancel();
    private async void DocumentHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Knowledge is not { } knowledge || !vaultImport.IsCompleted) return;
        try
        {
            var results = await Task.Run(() => DocumentImports.List(knowledge.Root));
            if (Knowledge != knowledge || closing) return;
            DocumentImportResults.ItemsSource = results; DocumentImportStatus.Text = $"{results.Length} saved imports (up to 1,000 shown). Originals and notes remain in the vault.";
        }
        catch (Exception ex) { DocumentImportStatus.Text = "Could not list imports: " + ex.Message; }
    }
    private void DocumentOriginal_Click(object sender, RoutedEventArgs e) => OpenImport(false);
    private void DocumentNote_Click(object sender, RoutedEventArgs e) => OpenImport(true);
    internal static string PdfNotices()
    {
        var assembly = typeof(PdfText).Assembly;
        return "PdfPig 0.1.16 · https://github.com/UglyToad/PdfPig\n\n" + string.Join("\n\n", assembly.GetManifestResourceNames().Where(n => n.Contains("Licenses.PdfPig")).Order().Select(name =>
        { using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!); return reader.ReadToEnd(); }));
    }
    private void PdfNotices_Click(object sender, RoutedEventArgs e)
    {
        new Window { Owner = this, Title = "PDF library notices", Width = 720, Height = 520, Content = new System.Windows.Controls.TextBox
            { Text = PdfNotices(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto } }.Show();
    }
    private void OpenImport(bool note)
    {
        if (Knowledge is not { } knowledge || DocumentImportResults.SelectedItem is not ImportResult { Document: { } item })
        { DocumentImportStatus.Text = "Select a successful import first."; return; }
        try
        {
            var current = DocumentImports.Read(knowledge.Root, "Imports/" + item.Id + "/import.json");
            var relative = note ? current.Note : current.Original;
            if (!File.Exists(VaultFiles.SafePath(knowledge.Root, relative))) throw new IOException("The selected file is missing.");
            OpenVaultNote(knowledge.Root, relative, note);
        }
        catch (Exception ex) { DocumentImportStatus.Text = "Could not open import: " + ex.Message; }
    }
}
