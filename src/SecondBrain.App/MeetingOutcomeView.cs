using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class MeetingOutcomeView : UserControl
{
    internal ListBox Items { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    internal TextBox DraftText { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 100 };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    internal MeetingOutcomes? Review { get; private set; }
    private readonly string directory;
    private readonly TranscriptDetail[] records;
    private readonly ComboBox category = new() { Width = 125, ItemsSource = Enum.GetValues<OutcomeKind>(), SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 6) };
    private readonly Action<string> goTo;
    private readonly Func<string?> selectedSegment;
    internal MeetingOutcomeView(string directory, TranscriptDetail[] records, Action<string> goTo, Func<string?> selectedSegment)
    {
        this.directory = directory; this.records = records; this.goTo = goTo; this.selectedSegment = selectedSegment;
        var grid = new Grid { Margin = new Thickness(12) };
        foreach (var size in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new() { Height = size });
        grid.Children.Add(new TextBlock { Text = "Outcomes & follow-up\nReview these suggested excerpts. Questions may already be answered elsewhere. Include only points you have checked; owners and dates are never inferred. You can add a missed item from the selected transcript turn.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Grid.SetRow(Items, 1); grid.Children.Add(Items); ScrollViewer.SetHorizontalScrollBarVisibility(Items, ScrollBarVisibility.Disabled);
        var template = new DataTemplate(typeof(MeetingOutcome));
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(MarginProperty, new Thickness(6));
        var meta = new FrameworkElementFactory(typeof(TextBlock)); meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Meta")); meta.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); panel.AppendChild(meta);
        var quote = new FrameworkElementFactory(typeof(TextBlock)); quote.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Quote")); quote.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); quote.SetValue(MarginProperty, new Thickness(0, 6, 0, 0)); panel.AppendChild(quote); template.VisualTree = panel; Items.ItemTemplate = template;
        var choices = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(choices, 2); grid.Children.Add(choices);
        Button Add(Panel parent, string text, Action click) { var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) }; button.Click += (_, _) => Act(click); parent.Children.Add(button); return button; }
        Add(choices, "Go to source", () => { if (Items.SelectedItem is MeetingOutcome item) goTo(item.SegmentId); });
        Add(choices, "Include in draft", () => Choose(OutcomeChoice.Include));
        Add(choices, "Dismiss", () => Choose(OutcomeChoice.Dismiss));
        Add(choices, "Mark answered", () => Choose(OutcomeChoice.Resolved));
        Add(choices, "Reset choice", () => Choose(OutcomeChoice.Pending));
        Add(choices, "Reload", Reload);
        var actions = new WrapPanel(); Grid.SetRow(actions, 3); grid.Children.Add(actions); actions.Children.Add(category);
        Add(actions, "Add selected transcript turn", AddSelected);
        Add(actions, "Build follow-up draft", BuildDraft);
        Add(actions, "Copy draft", CopyDraft);
        Grid.SetRow(DraftText, 4); grid.Children.Add(DraftText); Grid.SetRow(Status, 5); grid.Children.Add(Status); Content = grid;
        System.Windows.Automation.AutomationProperties.SetName(Items, "Source-linked outcome candidates");
        System.Windows.Automation.AutomationProperties.SetName(DraftText, "Editable follow-up draft");
        System.Windows.Automation.AutomationProperties.SetName(category, "Category for selected transcript turn");
        Act(Reload);
    }
    private void Act(Action action)
    { try { action(); } catch (Exception ex) { Status.Text = "Could not complete review: " + ex.Message; } }
    internal void Reload()
    {
        Review = null; Items.ItemsSource = null; DraftText.Clear(); Review = new(directory, records); Refresh();
        Status.Text = (Review.Items.Length == 0 ? "No automatic candidates. Select a turn in Transcript to add a source-linked item. " : "Choices save locally; reset any choice to review it again. ")
            + (Review.Limited ? "Some candidates exceeded limits and are omitted. " : "") + (Review.RecoveredTail ? "An incomplete last edit was ignored. " : "") + "Drafts are never sent automatically.";
    }
    private void Refresh()
    {
        var previous = Items.SelectedItem as MeetingOutcome; Items.ItemsSource = Review!.Items;
        Items.SelectedItem = Review.Items.FirstOrDefault(i => i.SegmentId == previous?.SegmentId && i.Kind == previous.Kind) ?? Review.Items.FirstOrDefault();
        DraftText.Clear();
    }
    internal void Choose(OutcomeChoice choice)
    {
        if (Review is null || Items.SelectedItem is not MeetingOutcome item) throw new InvalidOperationException("Select an outcome first.");
        Review.Set(item.SegmentId, item.Kind, choice); Refresh(); Status.Text = "Choice saved. Build the draft again to reflect this review.";
    }
    private void AddSelected()
    {
        if (Review is null || selectedSegment() is not { } id) throw new InvalidOperationException("Select a transcript turn first.");
        var kind = (OutcomeKind)category.SelectedItem; Review.Set(id, kind, OutcomeChoice.Pending); Refresh(); Items.SelectedItem = Review.Items.Single(i => i.SegmentId == id && i.Kind == kind);
        Status.Text = "Selected transcript excerpt added for review. Choose Include in draft if appropriate.";
    }
    internal void BuildDraft()
    {
        if (Review is null) throw new InvalidOperationException("Reload outcome review first.");
        DraftText.Text = Review.Draft(); Status.Text = DraftText.Text.Length == 0 ? "Include at least one reviewed item to build a draft." : "Draft ready to edit and copy. Exact quotes retain source wording; review before sharing.";
    }
    private void CopyDraft()
    {
        if (Review is null || Review.Draft().Length == 0 || string.IsNullOrWhiteSpace(DraftText.Text)) throw new InvalidOperationException("Build a draft from reviewed items first.");
        Clipboard.SetText(DraftText.Text); Status.Text = "Draft copied. Nothing was sent.";
    }
}
