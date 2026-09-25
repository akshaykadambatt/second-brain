using SecondBrain.Core;

internal static class QuietSuggestionTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Quiet suggestions enforce opt-in, one card, thirty-second spacing, dismissal and topic expiry", () =>
        {
            var suggestions = new QuietSuggestions(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
            check(!suggestions.Offer(a, "Clarify the source.", "one", 0), "Suggestion enabled by default");
            suggestions.Enabled = true; check(suggestions.Offer(a, "Clarify the source.", "one", 0), "First card missing");
            check(!suggestions.Offer(a, "Another card", "two", 1) && suggestions.Current!.Text == "Clarify the source.", "Card replaced within a topic");
            suggestions.Dismiss(); check(!suggestions.Offer(a, "Clarify the source.", "one", 31), "Dismissed topic resurfaced");
            check(suggestions.Offer(b, "Check the changed decision.", "two", 31), "New topic did not show after rate limit");
            suggestions.Topic(Guid.NewGuid()); check(suggestions.Current is null, "Old topic remained visible");
            check(!suggestions.Offer(Guid.NewGuid(), "Third card", "three", 40), "Rate limit bypassed by topic change");
            check(suggestions.Offer(Guid.NewGuid(), "Third card", "three", 61), "Next eligible card missing"); suggestions.Tick(91); check(suggestions.Current is null, "Expired card stayed visible");
            suggestions.Enabled = false; suggestions.Enabled = true; check(!suggestions.Offer(Guid.NewGuid(), "Third card", "three", 100), "Toggle bypassed content deduplication");
            var options = new AssistantOptions { QuietSuggestions = true }; var saved = System.Text.Json.JsonSerializer.Deserialize<AssistantOptions>(System.Text.Json.JsonSerializer.Serialize(options));
            check(saved!.QuietSuggestions && !new AssistantOptions().QuietSuggestions, "Suggestion preference persistence/default changed");
        });
    }
}
