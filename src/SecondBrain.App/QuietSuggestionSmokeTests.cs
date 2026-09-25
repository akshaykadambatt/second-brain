using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class QuietSuggestionSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var original = main.Session.Answer; var words = main.Session.Words.ToArray(); main.Session.Select(3); var position = main.Session.Position;
        main.SuggestionCheck.IsChecked = true;
        var topic = Guid.NewGuid(); var result = new KnowledgeResult([new(new("fixture", "client.md", 10, "Review", "The review is planned for Friday.", "", new(2026, 1, 2)), 1)], "Fixture source");
        main.OfferSuggestion(topic, result, 0);
        check(main.SuggestionCard.Visibility == Visibility.Visible && main.SuggestionSource.Text.Contains("client.md:10"), "Opt-in source reminder appears separately with provenance");
        check(main.Session.Answer == original && main.Session.Position == position && words.Zip(main.Session.Words).All(pair => ReferenceEquals(pair.First, pair.Second)), "Suggestion never selects, replaces or appends reader text");
        main.Suggestions.Dismiss(); main.RenderSuggestion(); check(main.SuggestionCard.Visibility == Visibility.Collapsed, "Dismissal removes the card");
        main.OfferSuggestion(Guid.NewGuid(), new([], "missing"), 10); check(main.Suggestions.Current is null, "New request cannot bypass thirty-second limit");
        main.OfferSuggestion(Guid.NewGuid(), new([], "missing"), 30); check(main.Suggestions.Current is not null, "Follow-up becomes available after interval");
        main.Suggestions.Tick(60); main.RenderSuggestion(); check(main.SuggestionCard.Visibility == Visibility.Collapsed, "Expired suggestion is hidden");
        new AssistantSettings(directory).Save(new() { QuietSuggestions = true }); check(new AssistantSettings(directory).Load().QuietSuggestions, "Optional preference survives reload");
        await main.Dispatcher.InvokeAsync(() => { }); capture(main, System.IO.Path.Combine(directory, "quiet-suggestions.png"));
    }
}
