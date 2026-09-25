using System.IO;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class ClientKnowledgeWindow : Window
{
    private readonly string root;
    private readonly Func<ClientKnowledge, string?, Task<KnowledgeDocument>> save;
    internal readonly ComboBox Clients = new() { DisplayMemberPath = "DisplayName" };
    internal readonly ListBox Records = new() { MinHeight = 160 };
    internal readonly ComboBox Kind = new() { ItemsSource = Enum.GetValues<KnowledgeKind>(), SelectedIndex = 0 };
    internal readonly TextBox NameField = new(), Aliases = new(), Project = new(), Body = new() { AcceptsReturn = true, MinHeight = 110, TextWrapping = TextWrapping.Wrap };
    internal readonly TextBox Source = new(), Line = new() { Text = "1" }, Quote = new() { AcceptsReturn = true, MinHeight = 65, TextWrapping = TextWrapping.Wrap }, Date = new(), OwnerField = new(), Due = new();
    internal readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap };
    private KnowledgeDocument? selected;
    private bool busy;
    internal ClientKnowledgeWindow(string root, SessionContext[] clients, Guid selectedClient, Func<ClientKnowledge, string?, Task<KnowledgeDocument>> save, Action<string> open, KnowledgeHit? source = null)
    {
        this.root = root; this.save = save; Title = "Client knowledge"; Width = 960; Height = 760; MinWidth = 700; MinHeight = 500;
        var grid = new Grid { Margin = new(18) }; grid.ColumnDefinitions.Add(new() { Width = new(270) }); grid.ColumnDefinitions.Add(new());
        var left = new StackPanel { Margin = new(0, 0, 16, 0) }; grid.Children.Add(left);
        left.Children.Add(new TextBlock { Text = "CLIENT KNOWLEDGE", FontSize = 22, FontWeight = FontWeights.Bold });
        left.Children.Add(new TextBlock { Text = "Choose a saved client. Create client briefs on Live first.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        left.Children.Add(Clients); left.Children.Add(Records);
        void Button(Panel panel, string text, Action action) { var b = new Button { Content = text, Margin = new(0, 6, 0, 0) }; b.Click += (_, _) => { if (!busy) action(); }; panel.Children.Add(b); }
        Button(left, "New record", Clear); Button(left, "Reload records", Reload);
        Button(left, "Open Markdown", () => { if (selected is not null) open(selected.Value.Relative); });
        Button(left, "Open source", () => { if (Source.Text.Length > 0) open(Source.Text); });
        left.Children.Add(Status);
        var form = new StackPanel(); var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); grid.Children.Add(scroll);
        void Field(string label, Control control) { form.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 4) }); form.Children.Add(control); }
        Field("Record type", Kind); Field("Name", NameField); Field("Aliases (one per line)", Aliases); Aliases.AcceptsReturn = true;
        Field("Project (optional)", Project); Field("Notes · Markdown", Body);
        Field("Source note · relative vault path", Source); Field("Source starting line", Line); Field("Exact source quote", Quote); Field("Source date · YYYY-MM-DD", Date);
        Field("Owner · exact source wording, or blank", OwnerField); Field("Due · exact source wording, or blank", Due);
        Button(form, "Save observation", () => _ = Save(false)); Button(form, "Confirm sourced record", () => _ = Save(true));
        form.Children.Add(new TextBlock { Text = "Save observation also reverses confirmation. Source matching checks provenance; you confirm whether the note accurately represents it. History is kept in the vault.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        Content = grid; Clients.ItemsSource = clients.Where(c => c.ProfileId != Guid.Empty).ToArray(); Clients.SelectedItem = clients.FirstOrDefault(c => c.ProfileId == selectedClient && c.ProfileId != Guid.Empty);
        Clients.SelectionChanged += (_, _) => { if (!busy) { Clear(); Reload(); } }; Records.SelectionChanged += (_, _) => { if (!busy && Records.SelectedItem is KnowledgeDocument row) Show(row); };
        Records.DisplayMemberPath = "Value"; Closing += (_, e) => { if (busy) { e.Cancel = true; Status.Text = "Finishing the current save…"; } };
        Reload();
        if (source is not null) { Source.Text = source.Chunk.File; Line.Text = source.Chunk.Line.ToString(); Quote.Text = source.Chunk.Text; Date.Text = source.Chunk.Date?.ToString("yyyy-MM-dd") ?? ""; }
    }
    internal void Reload()
    {
        try { var catalog = new ClientKnowledgeStore(root).List((Clients.SelectedItem as SessionContext)?.ProfileId ?? Guid.Empty); Records.ItemsSource = catalog.Documents; Status.Text = $"{catalog.Documents.Length} records. " + string.Join("\n", catalog.Problems); }
        catch (Exception ex) { Status.Text = "Could not read records: " + ex.Message; }
    }
    private void Clear() { selected = null; Records.SelectedItem = null; NameField.Clear(); Aliases.Clear(); Body.Clear(); Source.Clear(); Quote.Clear(); Date.Clear(); OwnerField.Clear(); Due.Clear(); Line.Text = "1"; Project.Text = (Clients.SelectedItem as SessionContext)?.Project ?? ""; }
    private void Show(KnowledgeDocument row)
    {
        selected = row; var v = row.Value; Kind.SelectedItem = v.Kind; NameField.Text = v.Name; Aliases.Text = string.Join('\n', v.Aliases); Project.Text = v.Project; Body.Text = v.Text;
        Source.Text = v.Source; Line.Text = v.SourceLine.ToString(); Quote.Text = v.Quote; Date.Text = v.Date?.ToString("yyyy-MM-dd") ?? ""; OwnerField.Text = v.Owner; Due.Text = v.Due;
        Status.Text = v.Status + " · Changes are saved only when you press a save button.";
    }
    internal async Task Save(bool confirmed)
    {
        if (busy) return; busy = true; Clients.IsEnabled = Records.IsEnabled = false;
        try
        {
            if (Clients.SelectedItem is not SessionContext client) throw new InvalidDataException("Choose a saved client first.");
            DateOnly? date = string.IsNullOrWhiteSpace(Date.Text) ? null : DateOnly.ParseExact(Date.Text.Trim(), "yyyy-MM-dd");
            var value = new ClientKnowledge { Id = selected?.Value.Id ?? Guid.NewGuid(), ClientId = client.ProfileId, Kind = (KnowledgeKind)Kind.SelectedItem,
                Name = NameField.Text.Trim(), Aliases = Aliases.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToArray(), Project = Project.Text.Trim(), Text = Body.Text,
                Source = Source.Text.Trim().Replace('\\', '/'), SourceLine = int.Parse(Line.Text), Quote = Quote.Text.Replace("\r", ""), Date = date, Owner = OwnerField.Text.Trim(), Due = Due.Text.Trim(), Status = confirmed ? "Confirmed" : "Observation" };
            selected = await save(value, selected?.Revision); Reload(); Show(selected); Status.Text = "Saved " + selected.Value.Status.ToLowerInvariant() + ". Markdown and history updated.";
        }
        catch (Exception ex) { Status.Text = "Not saved: " + ex.Message; }
        finally { busy = false; Clients.IsEnabled = Records.IsEnabled = true; }
    }
}
