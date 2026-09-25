using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    internal QuietSuggestions Suggestions { get; } = new();
    private void SuggestionCheck_Click(object sender, RoutedEventArgs e)
    {
        Suggestions.Enabled = SuggestionCheck.IsChecked == true; RenderSuggestion();
        try { companionSettings!.Save(CompanionOptions()); } catch (Exception) { CompanionStatus.Text = "Suggestion preference could not be saved."; }
    }
    private void SuggestionDismiss_Click(object sender, RoutedEventArgs e) { Suggestions.Dismiss(); RenderSuggestion(); }
    private void RefreshQuietSuggestions()
    {
        Suggestions.Enabled = SuggestionCheck.IsChecked == true; Suggestions.Tick(AudioClock.Now);
        if (Companion?.Active != true) Suggestions.EndSession();
        else if (Companion.Answers.LatestPrimary is { } run)
        {
            Suggestions.Topic(run.Id);
            if (AudioClock.Now - run.CreatedAt <= 45 && run.Knowledge is { } result) OfferSuggestion(run.Id, result, AudioClock.Now);
        }
        RenderSuggestion();
    }
    internal void OfferSuggestion(Guid topic, KnowledgeResult result, double now)
    {
        Suggestions.Enabled = SuggestionCheck.IsChecked == true;
        var first = result.Hits.FirstOrDefault()?.Chunk;
        var source = first is null ? "No supporting client source found" : $"{first.File}:{first.Line} · {first.Date?.ToString("yyyy-MM-dd") ?? "undated"} · {first.FactStatus}";
        var text = result.Conflicts.Length > 0 ? "Follow up: which dated account should we treat as current?"
            : first is null ? "Follow up: which source can confirm this detail?"
            : "Source reminder: " + (first.Text.Length <= 240 ? first.Text : first.Text[..240] + "…");
        Suggestions.Offer(topic, text, source, now); RenderSuggestion();
    }
    internal void RenderSuggestion()
    {
        SuggestionCard.Visibility = Suggestions.Current is null ? Visibility.Collapsed : Visibility.Visible;
        SuggestionText.Text = Suggestions.Current?.Text ?? ""; SuggestionSource.Text = Suggestions.Current?.Source ?? "";
    }
}
