using System.IO;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class OpeningTests
{
    private sealed class Lookup : IStagedKnowledgeSearch
    {
        internal readonly TaskCompletionSource<KnowledgeResult> Deep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<KnowledgeResult> SearchOpening(string question, CancellationToken cancellation) => Task.FromResult(new KnowledgeResult([], "Opening has no private evidence"));
        public Task<KnowledgeResult> Search(string question, CancellationToken cancellation) => Deep.Task.WaitAsync(cancellation);
        public Task Prewarm(string context, CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Provider : IAnswerProvider
    {
        internal readonly List<AssistantPrompt> Prompts = [];
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { Prompts.Add(prompt); await delta(prompt.Deeper ? "Supporting detail is now available.\n\n" : "That date is not established yet.\n\n"); }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check)
    {
        var lookup = new Lookup(); var provider = new Provider(); var answers = new AssistantService(main.Dispatcher, provider, new(directory), lookup);
        var request = answers.Ask("When is the release?", new(), "", Guid.NewGuid(), continuation: true);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while (request.Fast.WordCount == 0) await Task.Delay(10, timeout.Token);
        check(request.Fast.WordCount > 0 && !request.Work.IsCompleted && provider.Prompts.Count == 1, "Readable opening does not wait for deeper retrieval");
        var opening = request.Fast.Blocks[0];
        var note = new NoteChunk("fixture", "release.md", 1, "Release", "Release evidence", "Cedar", null);
        lookup.Deep.SetResult(new([new(note, 1)], "Deeper evidence ready")); await request.Work;
        check(provider.Prompts[1].Knowledge.Contains("Release evidence") && ReferenceEquals(opening, request.Fast.Blocks[0]) && answers.Inbox.Answers.Count == 1,
            "Deeper retrieval augments evidence and appends to the same opening");
        var root = Path.Combine(directory, "opening-vault"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "decision.md"); File.WriteAllText(path, "---\nproject: Cedar\n---\n# Release\nRelease is Friday.");
        using var service = new KnowledgeService(root, directory, new(), null); await service.Refresh(true);
        var scoped = (IStagedKnowledgeSearch)service.ForProject("Cedar");
        var first = await scoped.SearchOpening("release", CancellationToken.None); var cached = await scoped.SearchOpening("release", CancellationToken.None);
        check(ReferenceEquals(first, cached) && first.Hits.Count == 1, "Identical opening lookup reuses an indexed evidence snapshot");
        var excluded = await ((IStagedKnowledgeSearch)service.ForProject("Other")).SearchOpening("release", CancellationToken.None);
        check(excluded.Hits.Count == 0, "Opening cache separates project scope");
        File.WriteAllText(path, "---\nproject: Cedar\n---\n# Release\nRelease is Monday."); await service.Refresh(true);
        var changed = await scoped.SearchOpening("release", CancellationToken.None);
        check(changed.Evidence.Contains("Monday") && !changed.Evidence.Contains("Friday"), "Changed Markdown invalidates cached opening passages");
        File.Delete(path); await service.Refresh(true);
        check((await scoped.SearchOpening("release", CancellationToken.None)).Hits.Count == 0, "Deleted Markdown cannot survive in opening cache");
        await service.Stop();
    }
}
