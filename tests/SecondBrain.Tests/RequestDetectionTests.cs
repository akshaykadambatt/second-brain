using SecondBrain.Core;

internal static class RequestDetectionTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Directed requests trigger without question punctuation while ordinary plans and negations stay quiet", () =>
        {
            var entry = new TranscriptEntry(Guid.NewGuid(), "Final", AudioSource.System, 0, 1, "");
            foreach (var text in new[] { "Explain the rollout.", "Please compare these two approaches", "Walk us through the risks", "I'd like your recommendation on the schedule", "We need you to clarify the deadline", "Thanks for that. Give us an example." })
                check(QuestionGate.Detect(entry with { Text = text }) is not null, "Missed request: " + text);
            foreach (var text in new[] { "We need to prepare the rollout.", "I will explain the rollout.", "Please do not explain the rollout.", "Don't compare those options.", "They asked me to explain the rollout.", "We would like to review this internally.", "The comparison is in the report." })
                check(QuestionGate.Detect(entry with { Text = text }) is null, "False request: " + text);
            check(QuestionGate.Detect(entry with { Text = "Explain the rollout", Kind = "Partial" }) is null && QuestionGate.Detect(entry with { Text = "Explain the rollout", Source = AudioSource.Microphone }) is null, "Wrong speech source or partial triggered");
        });
        test("Request detection retains normalized deduplication and generated-answer echo suppression", () =>
        {
            var gate = new QuestionGate(); check(gate.Accept("Please explain the rollout.", 0, true), "First request suppressed");
            check(!gate.Accept("Please EXPLAIN the rollout!", 20, true), "Duplicate request accepted");
            gate.RememberAnswer("Please compare the options before deciding."); check(!gate.Accept("Please compare the options before deciding.", 30, true), "Generated request echo accepted");
            gate.Reset(); check(gate.Accept("Please explain the rollout.", 40, true), "New session remained suppressed");
        });
    }
}
