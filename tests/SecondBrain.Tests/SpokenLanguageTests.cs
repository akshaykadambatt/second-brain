using System.Text.Json;
using SecondBrain.Core;

internal static class SpokenLanguageTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Jargon preference defaults to conversational and validates all five persisted levels", () =>
        {
            check(new AssistantOptions().JargonLevel == 2 && JsonSerializer.Deserialize<AssistantOptions>("{}")!.JargonLevel == 2, "Legacy settings did not default to conversational");
            for (var level = 1; level <= 5; level++)
            {
                var saved = new AssistantOptions(AllowGeneralGuidance: true) { JargonLevel = level, QuietSuggestions = true };
                var loaded = JsonSerializer.Deserialize<AssistantOptions>(JsonSerializer.Serialize(saved));
                check(loaded == saved && loaded.IsValid, "Jargon setting or unrelated preferences lost");
                check(SpokenLanguage.Instruction(level).Contains("Preserve exact names, numbers, facts and uncertainty") && SpokenLanguage.Instruction(level).Contains(SpokenLanguage.Name(level)), "Style lost source qualifications or level identity");
            }
            check(!new AssistantOptions { JargonLevel = 0 }.IsValid && !new AssistantOptions { JargonLevel = 6 }.IsValid, "Invalid jargon level accepted");
        });
    }
}
