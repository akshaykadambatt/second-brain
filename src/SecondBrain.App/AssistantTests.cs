using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class AssistantTests
{
    private sealed class ControlledProvider : IAnswerProvider
    {
        internal sealed record Call(AssistantPrompt Prompt, Func<string, Task> Delta, TaskCompletionSource Done);
        public List<Call> Calls { get; } = [];
        public Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        {
            // Deliberately ignores cancellation: late callbacks must be harmless.
            var call = new Call(prompt, delta, new(TaskCreationOptions.RunContinuationsAsynchronously)); Calls.Add(call); return call.Done.Task;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<System.Windows.Window, string> capture)
    {
        var original = main.Session.Text; main.Session.Select(7); var position = main.Session.Position;
        main.ScriptEditor.Text = "Unapplied draft survives AI answers."; var draft = main.ScriptEditor.Text;
        while (main.Panels.Count < 4) main.AddReader();
        var fake = new ControlledProvider(); var window = main.OpenAssistant(fake); window.Deeper.IsChecked = true;
        var run = window.Ask("What should we verify first?")!;
        check(run is not null && main.Session.Answer is null, "Typed question queues without replacing the reader");
        await fake.Calls[0].Delta("A complete first");
        check(run!.Fast.WordCount == 0, "Partial text cannot reach reader");
        await fake.Calls[0].Delta(" paragraph.\n\n");
        await window.SelectAnswer(run.Fast); main.Session.Select(2);
        var word = main.Session.Words[2];
        await fake.Calls[0].Delta("Another complete paragraph.\n\n");
        check(main.Session.Position == 2 && main.Session.Words[2] == word && main.Panels.Count == 4, "Appending a readable block preserves selected word with four panels");
        fake.Calls[0].Done.SetResult(); await Until(() => fake.Calls.Count == 2);
        await fake.Calls[1].Delta("A deeper explanation follows.\n\n"); fake.Calls[1].Done.SetResult(); await run.Work;
        check(run.Fast.State == AnswerState.Complete && run.Deeper?.State == AnswerState.Complete && run.Deeper.ParentAnswerId == run.Fast.Id && run.Deeper.RequestId == run.Id && main.Session.Answer == run.Fast, "Quick/deeper answers retain association and deeper answer stays queued");
        window.Question.Text = "What should we verify first?";
        capture(window, Path.Combine(directory, "assistant.png"));
        var canceled = window.Ask("What happens during cancellation?")!;
        var canceledCall = fake.Calls[^1]; window.Service.CancelAll();
        var newer = window.Ask("What happens to the newer question?")!;
        await canceledCall.Delta("Late old answer must be ignored.\n\n"); canceledCall.Done.SetResult(); await canceled.Work;
        check(canceled.Fast.State == AnswerState.Superseded && canceled.Fast.WordCount == 0 && main.Session.Answer == run.Fast && newer.Active, "Late canceled response cannot corrupt old or newer answer");
        fake.Calls[^1].Done.SetException(new IOException("Injected connection failure.")); await newer.Work;
        check(newer.Fast.State == AnswerState.Failed && newer.Deeper?.State == AnswerState.Failed && main.Session.Position == 2, "Failed requests are visible without moving reader");
        var count = window.Service.Requests.Count; window.Automatic.IsChecked = true;
        var session = Guid.NewGuid();
        main.AssistantContext.Observe(new(session, "Final", AudioSource.Microphone, 0, 1, "How can we improve this?"));
        main.AssistantContext.Observe(new(session, "Provisional", AudioSource.System, 1, 2, "How can we improve this?"));
        await Task.Delay(600);
        check(window.Service.Requests.Count == count, "Microphone and provisional events cannot trigger an automatic answer");
        main.Playback.StartVoice();
        main.AssistantContext.Observe(new(session, "Final", AudioSource.System, 2, 3, "Why are we reading this?"));
        await Task.Delay(600); main.Playback.Pause();
        check(window.Service.Requests.Count == count, "Voice-following guard suppresses prompted-speech echo");
        main.AssistantContext.Observe(new(session, "Final", AudioSource.System, 3, 4, "What is the acceptance criterion?"));
        await Until(() => window.Service.Requests.Count > count);
        var auto = window.Service.Requests[^1];
        check(auto.SessionId == session && fake.Calls[^1].Prompt.Conversation.Contains("System") && main.Session.Answer == run.Fast, "Final computer question uses labeled current context and stays queued");
        var autoCall = fake.Calls[^1]; window.Close();
        await autoCall.Delta("A late paragraph after close.\n\n"); autoCall.Done.SetResult(); await window.ShutdownTask;
        check(main.Session.Text == original && main.Session.Position == position && main.ScriptEditor.Text == draft, "Closing cancels work and restores script, position and unapplied draft");
        var settings = new AssistantSettings(directory); var saved = new AssistantOptions(Context: "Known project context."); settings.Save(saved);
        check(settings.Load() == saved, "AI model and context settings survive independent reload");
        await AdapterTests(check);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static async Task AdapterTests(Action<bool, string> check)
    {
        static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        var body = Event(new { type = "response.created", sequence_number = 0, response = new { id = "fixture" } })
            + Event(new { type = "response.output_text.delta", sequence_number = 1, delta = "A safe synthetic answer." })
            + Event(new { type = "response.completed", sequence_number = 2, response = new { id = "fixture" } });
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = json.RootElement;
            check(request.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/responses" && request.Headers.Authorization?.Scheme == "Bearer" && !root.GetProperty("store").GetBoolean() && root.GetProperty("stream").GetBoolean(), "Provider posts authenticated streaming request with storage disabled");
            check(root.GetProperty("model").GetString() == "fixture-model" && root.GetProperty("input").GetString()!.Contains("Known context"), "Configured model and explicit context reach provider adapter");
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        });
        using var provider = new OpenAiAnswerProvider(() => "synthetic-test-key", handler);
        var prompt = new AssistantPrompt(Guid.NewGuid(), "What is known?", "Known context", "", "fixture-model", "none", false);
        var answer = ""; await provider.Generate(prompt, text => { answer += text; return Task.CompletedTask; }, default);
        check(answer == "A safe synthetic answer.", "SSE adapter emits delta once and requires completion");
        using var guidance = new OpenAiAnswerProvider(() => "synthetic-test-key", new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var instructions = json.RootElement.GetProperty("instructions").GetString()!;
            check(instructions.Contains("Do not stop merely") && instructions.Contains("Never invent client facts") && instructions.Contains("hypothetical"), "Approved continuation adds general guidance without inventing client facts");
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }));
        await guidance.Generate(prompt with { Extension = true, AllowGeneralGuidance = true }, _ => Task.CompletedTask, default);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            using var error = new OpenAiAnswerProvider(() => "synthetic-test-key", new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
            try { await error.Generate(prompt, _ => Task.CompletedTask, default); check(false, "HTTP failure accepted"); }
            catch (InvalidOperationException ex) { check(!ex.Message.Contains("synthetic-test-key"), "HTTP " + (int)status + " fails visibly without exposing credential"); }
        }
        using var truncated = new OpenAiAnswerProvider(() => "synthetic-test-key", new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n", Encoding.UTF8, "text/event-stream") })));
        try { await truncated.Generate(prompt, _ => Task.CompletedTask, default); check(false, "Truncated stream accepted"); }
        catch (IOException) { check(true, "Truncated HTTP stream fails instead of implying completion"); }
    }
    public static async Task RunLive(MainWindow main, string directory, Action<bool, string> check)
    {
        var window = main.OpenAssistant();
        window.ContextNotes.Text = "Synthetic test project Cedar has a blue status and its next milestone is a reader trial on Friday. No other project facts are known.";
        var run = window.Ask("What is Cedar's status and next milestone?")!;
        await run.Work;
        File.WriteAllText(Path.Combine(directory, "assistant-latency.json"), JsonSerializer.Serialize(new { run.FirstTextMs, run.FirstReadableMs, run.CompletedMs, run.Status, FastState = run.Fast.State.ToString(), DeepState = run.Deeper?.State.ToString(), FastModel = window.FastModel.Text, DeepModel = window.DeepModel.Text }, new JsonSerializerOptions { WriteIndented = true }));
        check(run.Fast.State == AnswerState.Complete, "Live quick model produced complete readable paragraphs: " + run.Status);
        check(run.Deeper?.State == AnswerState.Complete, "Live deeper model produced associated readable paragraphs");
        var text = string.Join(" ", run.Fast.Blocks.Select(b => b.Text));
        check(text.Contains("blue", StringComparison.OrdinalIgnoreCase) && text.Contains("Friday", StringComparison.OrdinalIgnoreCase), "Live answer uses supplied synthetic facts");
        check(main.Session.Answer is null && run.FirstReadableMs > 0 && run.CompletedMs >= run.FirstReadableMs, "Live answer stays queued and records measured latency");
        window.Close(); await window.ShutdownTask;
    }
}
