using SecondBrain.Core;

internal static class MeetingMemoryTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Long meeting context keeps old decisions with source IDs beyond the recent buffer", () =>
        {
            var context = new AssistantContext(); var session = Guid.NewGuid();
            var decision = new TranscriptEntry(session, "Final", AudioSource.System, 12, 15, "We decided to postpone Cobalt until Friday.", Id: "old-decision"); context.Observe(decision);
            for (var i = 0; i < 500; i++) context.Observe(new(session, "Final", AudioSource.System, i + 20, i + 21, "Ordinary discussion about the current slide.", Id: "turn" + i));
            var snapshot = context.Snapshot(); check(snapshot.Contains("old-decision") && snapshot.Contains("postpone Cobalt") && snapshot.Contains("12.00"), "Old decision or provenance disappeared");
            context.Observe(decision); check(context.Memory.Excerpts.Count(e => e.Id == "old-decision") == 1, "Late duplicate duplicated memory");
            check(snapshot.Length < 30000 && context.Memory.Excerpts.Count <= 40, "Meeting context grew without bounds");
            context.Observe(new(Guid.NewGuid(), "RunStart", null, 0, 0, "")); check(context.Snapshot() == "", "Memory crossed a session boundary");
        });
        test("Memory keeps contradictory source wording and exposes omission instead of resolving facts", () =>
        {
            var memory = new MeetingMemory(); var session = Guid.NewGuid();
            memory.Observe(new(session, "Final", AudioSource.System, 1, 2, "The deadline is Friday.", Id: "one"));
            memory.Observe(new(session, "Final", AudioSource.System, 3, 4, "The deadline is Monday.", Id: "two"));
            check(memory.Snapshot().Contains("Friday") && memory.Snapshot().Contains("Monday"), "Conflicting sources silently collapsed");
            for (var i = 0; i < 100; i++) memory.Observe(new(session, "Final", AudioSource.Microphone, i + 5, i + 6, "A decision: " + new string('x', 900), Id: "large" + i));
            check(memory.Omitted && memory.Excerpts.Count <= 40 && memory.Snapshot().Length < 9000, "Memory limit or omission reporting failed");
        });
    }
}
