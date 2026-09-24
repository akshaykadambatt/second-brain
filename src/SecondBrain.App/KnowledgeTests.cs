using System.IO;
using System.Text.Json;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class KnowledgeTests
{
    private sealed class Embeddings : IEmbeddingProvider
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<float[][]> Embed(IReadOnlyList<string> inputs, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Calls++;
            if (Fail) throw new IOException("Injected embedding outage");
            return Task.FromResult(inputs.Select(s => { var v = new float[512]; v[s.Contains("Firewall") ? 1 : 0] = 1; return v; }).ToArray());
        }
    }
    private sealed class Provider : IAnswerProvider
    {
        public List<AssistantPrompt> Prompts { get; } = [];
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { Prompts.Add(prompt); cancellation.ThrowIfCancellationRequested(); await delta("An opening with a separate source list.\n\n"); }
    }
    private sealed class Delayed : IKnowledgeSearch
    {
        public TaskCompletionSource<KnowledgeResult> Result { get; } = new();
        public Task<KnowledgeResult> Search(string question, CancellationToken cancellation) => Result.Task;
    }
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var root = main.Knowledge!.Root;
        await main.Knowledge.Refresh(true);
        var note = Path.Combine(root, "benefits.md");
        File.WriteAllText(note, "---\nproject: Cedar\ndate: 2026-09-24\n---\n# Benefits\nEmployees receive twenty days of paid leave annually.");
        File.WriteAllText(Path.Combine(root, "network.md"), "# Network\nFirewall access requires security review.");
        var embeddings = new Embeddings();
        using var service = new KnowledgeService(root, directory, new("Cedar"), embeddings);
        await service.Refresh(true);
        var result = await service.Search("vacation allowance", CancellationToken.None);
        check(result.Hits.Count == 1 && result.Hits[0].Chunk.File == "benefits.md" && result.Status.Contains("semantic"), "Hybrid paraphrase retrieves source with project/date provenance");
        using (var restored = new KnowledgeService(root, directory, new(), embeddings))
        { var calls = embeddings.Calls; await restored.Refresh(true); check(embeddings.Calls == calls, "Persisted vectors avoid re-embedding unchanged notes after restart"); await restored.Stop(); }
        embeddings.Fail = true;
        check((await service.Search("paid leave", CancellationToken.None)).Hits.Count == 1, "Embedding outage falls back to keyword evidence");
        embeddings.Fail = false;
        await service.Rebuild(); check(service.Chunks.Any(c => c.File == "benefits.md"), "Deleted index rebuilds from source Markdown");
        File.WriteAllText(note, "---\nproject: Cedar\n---\n# New policy\nZircon schedule is Tuesday.");
        await service.Refresh(true);
        check((await service.Search("Zircon", CancellationToken.None)).Hits.Any(h => h.Chunk.Text.Contains("Tuesday")) && service.Chunks.All(c => !c.Text.Contains("twenty days")), "Manual edit replaces indexed content without retaining stale chunks");
        File.Delete(note); await service.Refresh(true);
        check(service.Chunks.All(c => c.File != "benefits.md"), "Deleted notes leave the retrieval index");
        await service.Stop();

        File.WriteAllText(Path.Combine(root, "decision.md"), "---\nproject: Cedar\ndate: 2026-09-24\n---\n# Decision\nThe Cedar release token is VIOLET-731. [[Home]]");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            while (!main.Knowledge.Chunks.Any(c => c.File == "decision.md")) await Task.Delay(50, deadline.Token);
        check(main.Knowledge.Chunks.Any(c => c.File == "decision.md"), "Background refresh notices an external Markdown edit without a rebuild click");
        var provider = new Provider(); var answers = new AssistantService(main.Dispatcher, provider, new(directory), main.Knowledge);
        var request = answers.Ask("What is Cedar release token?", new(), "", Guid.NewGuid(), continuation: true); await request.Work;
        check(provider.Prompts.Count == 2 && provider.Prompts.All(p => p.Knowledge.Contains("VIOLET-731") && p.Knowledge.Contains("2026-09-24")) && request.Knowledge!.Hits.Count > 0 && request.RetrievalMs.HasValue, "Opening and continuation share the same dated evidence snapshot");
        check(request.Fast.Blocks.All(b => !b.Text.Contains("decision.md")), "Source metadata stays separate from reader paragraphs");
        var missing = answers.Ask("zzunmatchedsecret", new(Deeper: false), "", Guid.NewGuid()); await missing.Work;
        check(provider.Prompts[^1].Knowledge.Contains("No relevant vault evidence"), "Missing evidence reaches the provider explicitly");
        var delayed = new Delayed(); var canceledProvider = new Provider(); var canceled = new AssistantService(main.Dispatcher, canceledProvider, new(directory), delayed);
        var pending = canceled.Ask("What comes next?", new(), "", Guid.NewGuid()); canceled.CancelAll(); delayed.Result.SetResult(new([], "Delayed")); await pending.Work;
        check(canceledProvider.Prompts.Count == 0 && pending.Fast.Blocks.Count == 0, "Canceled retrieval cannot start a late AI request");
        var settings = new VaultSettings(directory, directory); settings.Save(new());
        check(settings.Load().Folder == "Vault" && new VaultSettings(directory, Path.Combine(directory, "relocated")).Resolve(settings.Load()).EndsWith(Path.Combine("relocated", "Vault")), "Relative vault selection relocates with the executable folder");
        main.KnowledgeTab.IsSelected = true; main.VaultQuery.Text = "Cedar release token";
        main.VaultSearch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        for (var i = 0; i < 100 && !main.VaultSearch.IsEnabled; i++) await Task.Delay(20);
        check(main.VaultResults.Items.Count > 0, "Knowledge UI displays searchable source excerpts");
        capture(main, Path.Combine(directory, "knowledge.png"));
    }
    public static async Task RunLive(MainWindow main, string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "live-vault"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "benefits.md"), "---\nproject: Cedar\ndate: 2026-09-24\n---\n# Benefits\nEmployees receive twenty days of paid leave annually.");
        File.WriteAllText(Path.Combine(root, "decision.md"), "---\nproject: Cedar\ndate: 2026-09-24\n---\n# Release\nThe Cedar release token is VIOLET-731.");
        using var knowledge = new KnowledgeService(root, directory, new(), new OpenAiEmbeddings(new ApiKeyStore(directory, "OpenAI").Load));
        using var provider = new OpenAiAnswerProvider(new ApiKeyStore(directory, "OpenAI").Load);
        await knowledge.Refresh(true);
        var paraphrase = await knowledge.Search("vacation allowance", CancellationToken.None);
        File.WriteAllText(Path.Combine(directory, "semantic-result.json"), JsonSerializer.Serialize(new { knowledge.Status, search = paraphrase.Status, hits = paraphrase.Hits.Select(h => new { h.Chunk.File, h.Score }) }));
        check(paraphrase.Status.Contains("Keyword + semantic") && paraphrase.Hits.Any(h => h.Chunk.File == "benefits.md"), "Real embeddings retrieve a vacation paraphrase from paid-leave evidence");
        var service = new AssistantService(main.Dispatcher, provider, new(directory), knowledge); var evidence = new List<object>();
        async Task<string> Ask(string question)
        {
            var run = service.Ask(question, new(Deeper: false), "", Guid.NewGuid(), continuation: true); await run.Work;
            var text = string.Join("\n", run.Fast.Blocks.Select(b => b.Text));
            evidence.Add(new { question, text, run.RetrievalMs, run.FirstReadableMs, state = run.Fast.State.ToString(), sources = run.Knowledge?.Hits.Select(h => new { h.Chunk.File, h.Chunk.Date }) });
            File.WriteAllText(Path.Combine(directory, "knowledge-live-results.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            check(run.Fast.State == AnswerState.Complete, "Real grounded answer completes: " + run.Status); return text;
        }
        var known = await Ask("What is Cedar's release token?");
        check(known.Contains("VIOLET-731", StringComparison.OrdinalIgnoreCase), "Real answer uses the private synthetic fact from the vault");
        var absent = await Ask("What is our confidential payroll access code? If the evidence does not say, say you don't know.");
        check(new[] { "don't", "doesn’t", "doesn't", "not", "unknown", "no ", "cannot", "can't" }.Any(s => absent.Contains(s, StringComparison.OrdinalIgnoreCase)) && !absent.Contains("VIOLET-731"), "Real missing-evidence fixture admits the payroll code is unknown");
        File.WriteAllText(Path.Combine(root, "conflict.md"), "---\nproject: Cedar\ndate: 2026-09-24\n---\n# Release\nThe Cedar release token is AMBER-842. This record does not supersede the other same-day record.");
        await knowledge.Refresh(true);
        var conflict = await Ask("Which Cedar release token is confirmed? If same-day notes disagree, explicitly report the conflict.");
        check(new[] { "conflict", "disagree", "inconsisten", "unconfirmed", "not confirmed", "neither", "no confirmed", "can't confirm", "cannot confirm" }.Any(s => conflict.Contains(s, StringComparison.OrdinalIgnoreCase)), "Real conflicting-evidence fixture reports uncertainty rather than choosing a token");
        await service.Stop(); await knowledge.Stop();
    }
}
