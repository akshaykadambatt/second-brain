using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class JargonSmokeTests
{
    private sealed class Provider : IAnswerProvider
    {
        internal readonly List<AssistantPrompt> Prompts = [];
        public Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        {
            Prompts.Add(prompt);
            return delta(prompt.Extension ? "A useful next step is to check one small example together before making a bigger change. Keep the same facts and explain any uncertainty clearly.\n\n"
                : "We can start with a small test and see what works. The team can agree on the result it wants, try the change, and compare it with what happens today. That gives everyone something clear to discuss before taking the next step.\n\n");
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var reader = main.AddReader(); var sibling = main.AddReader(); reader.Width = 340;
        check(main.JargonLevel == 2 && reader.JargonText.Text == "Jargon 2/5", "Existing settings default to conversational reader speech");
        reader.JargonDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(main.JargonLevel == 1 && !reader.JargonDown.IsEnabled && sibling.JargonText.Text == "Jargon 1/5", "Reader minus button lowers the shared level and stops at Plain");
        for (var i = 0; i < 5; i++) reader.JargonUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(main.JargonLevel == 5 && !reader.JargonUp.IsEnabled && new AssistantSettings(directory).Load().JargonLevel == 5, "Reader plus button caps at Specialist and persists for restart");
        var newer = main.AddReader(); check(newer.JargonText.Text == "Jargon 5/5", "New reader panels inherit the saved level");
        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        var provider = new Provider(); check(await main.StartCompanion(provider), "Companion starts with the selected speaking style");
        var id = RecordingSession.ReadManifest(main.Recorder.LastDirectory!).Id;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4))) while (main.AssistantContext.SessionId != id) await Task.Delay(20, timeout.Token);
        var answer = main.Companion!.Ask("How can we test the change?")!; await answer.Work;
        check(provider.Prompts.Count >= 2 && provider.Prompts.All(p => p.JargonLevel == 5), "Selected level reaches both the opening and initial continuation");
        main.Session.Select(8); var words = main.Session.Words.ToArray(); var selected = main.Session.Answer;
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        reader.SetChromeVisibility(true); reader.UpdateLayout(); var viewport = reader.ReadingArea.ActualHeight; var before = reader.WordScreenY(8);
        for (var i = 0; i < 4; i++) reader.JargonDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        reader.UpdateLayout();
        check(main.JargonLevel == 1 && main.Session.Answer == selected && main.Session.Position == 8 && main.Playback.Voice.Active && main.Session.Words.SequenceEqual(words), "Changing jargon preserves answer identity, words, reading position and voice following");
        check(reader.ReadingArea.ActualHeight == viewport && Math.Abs(reader.WordScreenY(8) - before) < 1 && reader.JargonControls.TranslatePoint(new Point(reader.JargonControls.ActualWidth, 0), reader.HeaderChrome).X < reader.HeaderChrome.ActualWidth,
            "The jargon meter fits a minimum-width reader without reflowing its text");
        check(main.Companion.Answers.Extend(answer), "Current answer remains eligible for appended continuation"); await answer.Work;
        check(provider.Prompts[^1].Extension && provider.Prompts[^1].JargonLevel == 1 && main.Session.Answer == selected && words.Zip(main.Session.Words).All(pair => ReferenceEquals(pair.First, pair.Second)), "The next appended section uses the new level without rewriting earlier words");
        var variant = await main.RefineAnswer(AnswerRefinement.Explain, provider);
        check(variant is not null && provider.Prompts[^1].Refinement == AnswerRefinement.Explain && provider.Prompts[^1].JargonLevel == 1, "Answer variants use the current jargon level too");
        await main.StopCompanion(); reader.SetChromeVisibility(true); capture(reader, Path.Combine(directory, "jargon-reader.png"));
        var settingsFile = Path.Combine(directory, "assistant-settings.json"); var saved = File.ReadAllText(settingsFile); File.WriteAllText(settingsFile, "invalid fixture");
        check(!main.ChangeJargon(1) && main.JargonLevel == 1 && File.ReadAllText(settingsFile) == "invalid fixture", "A failed preference save retains the active level and original file"); File.WriteAllText(settingsFile, saved);
        static string Event(object item) => "data: " + JsonSerializer.Serialize(item) + "\n\n";
        var events = Event(new { type = "response.created", sequence_number = 0, response = new { id = "style-fixture" } })
            + Event(new { type = "response.output_text.delta", sequence_number = 1, delta = "A complete synthetic response." })
            + Event(new { type = "response.completed", sequence_number = 2, response = new { id = "style-fixture" } });
        for (var level = 1; level <= 5; level++)
        {
            var expected = level;
            using var adapter = new OpenAiAnswerProvider(() => "synthetic-key", new Handler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var instructions = body.RootElement.GetProperty("instructions").GetString()!;
                check(instructions.Contains(SpokenLanguage.Instruction(expected)) && instructions.Contains("Do not invent") && !body.RootElement.GetProperty("store").GetBoolean(), "Provider style instruction and grounding survive at level " + expected);
                return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
            }));
            var prompt = new AssistantPrompt(Guid.NewGuid(), "Explain the example?", "", "", "fixture", "none", false) { JargonLevel = level };
            await adapter.Generate(prompt, _ => Task.CompletedTask, default);
            await adapter.Generate(prompt with { Extension = true, Continuation = true, Deeper = true }, _ => Task.CompletedTask, default);
            await adapter.Generate(prompt with { Refinement = AnswerRefinement.Explain }, _ => Task.CompletedTask, default);
        }
    }
}
