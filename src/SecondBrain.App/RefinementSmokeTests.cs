using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class RefinementSmokeTests
{
    private sealed class Provider : IAnswerProvider
    {
        public readonly List<AssistantPrompt> Calls = [];
        public TaskCompletionSource? Hold;
        public async Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        {
            Calls.Add(prompt);
            if (prompt.Refinement is not null && Hold is { } held) await held.Task.WaitAsync(cancellation);
            await delta(prompt.Refinement is not null ? "This separate variant preserves the supplied source qualification.\n\n" : "The source says the review is planned for Friday, pending confirmation.\n\n");
        }
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var id = Guid.NewGuid(); main.AssistantContext.Observe(new(id, "RunStart", null, 0, 0, "")); var provider = new Provider();
        var service = new AssistantService(main.Dispatcher, provider, new DiagnosticLog(directory));
        using var companion = new CompanionSession(main.Session, main.Playback, main.AssistantContext, service, new(Deeper: false));
        var original = companion.Ask("When is the review?")!; await original.Work;
        original.Knowledge = new([new(new("source", "client.md", 4, "Review", "Review Friday, pending confirmation.", "Cedar", new(2026, 1, 2)), 1)], "Scoped fixture");
        var words = main.Session.Words.ToArray(); provider.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var variant = service.Refine(original, original.Fast, AnswerRefinement.Shorter);
        check(variant.Fast.ParentAnswerId == original.Fast.Id && variant.Id != original.Id, "Variant retains parent linkage and distinct request identity");
        var newer = companion.Ask("What is the next decision?")!; await newer.Work;
        provider.Hold.SetResult(); await variant.Work; provider.Hold = null;
        check(main.Session.Answer == newer.Fast && service.LatestPrimary == newer, "A late variant cannot steal selection from a newer readable answer");
        check(original.Fast.Blocks.SelectMany(b => b.Words).Zip(words).All(pair => ReferenceEquals(pair.First, pair.Second)), "Refinement preserves original reader word identities");
        var prompt = provider.Calls.Single(p => p.Refinement == AnswerRefinement.Shorter);
        check(prompt.OriginalAnswer.Contains("pending confirmation") && prompt.Knowledge.Contains("client.md") && !prompt.Continuation, "Variant receives frozen source answer, evidence and separate-answer intent");
        var explanation = service.Refine(newer, newer.Fast, AnswerRefinement.Explain); await explanation.Work;
        check(service.Extend(newer), "A variant does not block continuation of the newest primary answer"); await newer.Work;
        provider.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = service.Refine(newer, newer.Fast, AnswerRefinement.Example);
        await companion.Stop(); check(!cancelled.Active && cancelled.Cancellation.IsCancellationRequested, "Stopping cancels pending refinements");
        capture(main, System.IO.Path.Combine(directory, "answer-variants.png"));
    }
}
