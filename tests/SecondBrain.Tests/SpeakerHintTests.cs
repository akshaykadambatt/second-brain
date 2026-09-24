using SecondBrain.Core;

internal static class SpeakerHintTests
{
    internal static TranscriptDetail Fixture(Guid session) => new(1, session, "turn1", AudioSource.System, Guid.NewGuid(), 1, 3, "Useful guidance",
        [new("a", "Useful", 1, 1.8, .9f, 0, null, "remote1", "Speaker 1"), new("b", "guidance", 2, 3, .9f, 0, null, "remote1", "Speaker 1")]);
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Teams adapter requires secure hosts and explicit unique-roster speaking labels", () =>
        {
            check(TeamsSpeakingLabels.SupportedAddress("https://teams.cloud.microsoft/v2") && TeamsSpeakingLabels.SupportedAddress("teams.microsoft.com/v2"), "Teams host rejected");
            foreach (var host in new[] { "https://teams.microsoft.com.example.test", "https://teams.microsoft.com@example.test", "http://teams.microsoft.com", "https://meet.google.com/meeting", "https://teams.microsoft.com:444/" })
                check(!TeamsSpeakingLabels.SupportedAddress(host), "Unsupported host accepted");
            check(TeamsSpeakingLabels.Name("Morgan is speaking", ["Morgan", "Taylor"]) == "Morgan", "Speaking label lost");
            foreach (var label in new[] { "Morgan", "Morgan is not speaking", "Morgan is speaking in chat", "Someone is speaking", "Speaking: Morgan and Taylor" })
                check(TeamsSpeakingLabels.Name(label, ["Morgan", "Taylor"]) is null, "Ambiguous label named");
            check(TeamsSpeakingLabels.Name("Morgan is speaking", ["Morgan", "morgan"]) is null, "Duplicate roster identity accepted");
        });
        test("Screen hints require stable temporal coverage and one certain audio speaker", () =>
        {
            var session = Guid.NewGuid(); var detail = Fixture(session); var timeline = new SpeakerHintTimeline(session);
            for (var time = 1d; time <= 3; time += .5) timeline.Observe(time, ["Morgan"]);
            check(timeline.Resolve(detail)?.Name == "Morgan", "Stable covered turn was not named");
            check(timeline.Resolve(detail with { SessionId = Guid.NewGuid() }) is null, "Cross-session hint leaked");
            check(timeline.Resolve(detail with { Source = AudioSource.Microphone }) is null, "Microphone identity replaced");
            check(timeline.Resolve(detail with { Words = [detail.Words[0], detail.Words[1] with { SpeakerId = "remote2" }] }) is null, "Mixed speakers named");
            check(timeline.Resolve(detail with { Words = [detail.Words[0] with { SpeakerId = null }, detail.Words[1]] }) is null, "Unknown audio speaker named");
            check(timeline.Resolve(detail with { Words = [detail.Words[0], detail.Words[1] with { Start = 1.2 }] }) is null, "Overlapping words named");
            timeline.Clear(); timeline.Observe(2.5, ["Morgan"]); timeline.Observe(3, ["Morgan"]);
            check(timeline.Resolve(detail) is null, "Uncovered early speech named");
            timeline.Clear(); for (var time = 1d; time <= 3; time += .5) timeline.Observe(time, time == 2 ? ["Morgan", "Taylor"] : ["Morgan"]);
            check(timeline.Resolve(detail) is null, "Visual overlap named");
            timeline.Clear(); timeline.Observe(1, ["Morgan"]); timeline.Observe(3, ["Morgan"]);
            check(timeline.Resolve(detail) is null, "Stale gap named");
            timeline.Clear(); check(timeline.Resolve(detail) is null, "Cleared context retained names");
        });
        test("Speaker hints preserve original bindings and manual corrections always win", () =>
        {
            var path = folder("screen-hints"); var detail = Fixture(Guid.NewGuid()); var review = new TranscriptReview(path, [detail]);
            review.Assign(["a"], "Manual Morgan");
            var hint = new SpeakerNameHint(1, detail.SessionId, detail.SegmentId, SpeakerNameHints.Binding(detail), "Taylor", 1, 3);
            var writer = new SpeakerNameHints(path, detail.SessionId); writer.Append(detail, hint); writer.Append(detail, hint);
            check(File.ReadAllLines(Path.Combine(path, SpeakerNameHints.FileName)).Length == 1, "Duplicate hint appended");
            var opened = new TranscriptReview(path, [detail]);
            check(opened.Words[0].Speaker == "Manual Morgan" && opened.Words[1].Speaker == "Taylor (screen hint)", "Hint overrode manual correction");
            opened.Undo(); check(opened.Words.All(w => w.Speaker == "Taylor (screen hint)"), "Undo lost underlying hints");
            check(detail.Words.All(w => w.SpeakerLabel == "Speaker 1"), "Original audio labels rewritten");
            check(SpeakerNameHints.Read(path, [detail with { Text = "changed original" }], out var warning).Count == 0 && warning.Length > 0, "Changed source accepted");
        });
        test("Meet hints have independent URL label timing and persisted provenance qualification", () =>
        {
            check(MeetSpeakingLabels.SupportedAddress("https://meet.google.com/abc-defg-hij?authuser=1"), "Meet room rejected");
            foreach (var url in new[] { "https://meet.google.com/", "https://meet.google.com/landing", "https://meet.google.com.example.test/abc-defg-hij", "http://meet.google.com/abc-defg-hij", "https://teams.microsoft.com/abc-defg-hij" })
                check(!MeetSpeakingLabels.SupportedAddress(url), "Non-Meet room accepted");
            check(MeetSpeakingLabels.Name("Morgan (speaking)", ["Morgan"]) == "Morgan" && TeamsSpeakingLabels.Name("Morgan (speaking)", ["Morgan"]) is null, "Platform label contracts are coupled");
            foreach (var label in new[] { "Morgan", "Morgan (muted)", "Morgan is not speaking", "Speaking: Unknown", "Morgan (speaking) in chat" })
                check(MeetSpeakingLabels.Name(label, ["Morgan"]) is null, "Meet ambiguous label accepted");
            var detail = Fixture(Guid.NewGuid()); var timeline = new SpeakerHintTimeline(detail.SessionId);
            for (var time = 1d; time <= 3; time += .5) timeline.Observe(time, ["Morgan"], MeetSpeakingLabels.Adapter);
            var hint = timeline.Resolve(detail); check(hint?.Adapter == MeetSpeakingLabels.Adapter, "Meet provenance lost");
            var path = folder("meet-hints"); new SpeakerNameHints(path, detail.SessionId).Append(detail, hint!);
            check(SpeakerNameHints.Read(path, [detail], out var warning).Count == 2 && warning.Length == 0, "Meet sidecar rejected");
            timeline.Clear();
            for (var time = 1d; time <= 3; time += .5) timeline.Observe(time, ["Morgan"], time < 2 ? MeetSpeakingLabels.Adapter : "chrome-teams-speaking-en-v1");
            check(timeline.Resolve(detail) is null, "Changing meeting platform crossed a turn");
            timeline.Clear(); for (var time = 1d; time <= 3; time += .5) timeline.Observe(time, time == 2 ? ["Morgan", "Taylor"] : ["Morgan"], MeetSpeakingLabels.Adapter);
            check(timeline.Resolve(detail) is null, "Meet overlap named");
        });
        test("Corrupt optional hints cannot hide the original transcript or disable corrections", () =>
        {
            var path = folder("hint-recovery"); var detail = Fixture(Guid.NewGuid());
            var hint = new SpeakerNameHint(1, detail.SessionId, detail.SegmentId, SpeakerNameHints.Binding(detail), "Morgan", 1, 3);
            new SpeakerNameHints(path, detail.SessionId).Append(detail, hint);
            var file = Path.Combine(path, SpeakerNameHints.FileName); File.AppendAllText(file, "{interrupted");
            check(SpeakerNameHints.Read(path, [detail], out var warning).Count == 2 && warning.Length > 0, "Completed hint lost to partial tail");
            File.AppendAllText(file, "\n"); var bytes = File.ReadAllBytes(file);
            var review = new TranscriptReview(path, [detail]);
            check(review.HintWarning.Length > 0 && review.Words.All(w => w.Speaker == "Speaker 1"), "Corrupt hints did not fall back");
            review.Assign(["a"], "Manual"); check(new TranscriptReview(path, [detail]).Words[0].Speaker == "Manual", "Manual review disabled");
            check(bytes.SequenceEqual(File.ReadAllBytes(file)), "Corrupt evidence changed");
        });
    }
}
