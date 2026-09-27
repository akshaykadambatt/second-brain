using SecondBrain.Core;

internal static class MeetingOutcomeTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        TranscriptDetail[] Seed(string dir)
        {
            var id = Guid.NewGuid();
            using (var log = new TranscriptLog(dir, id))
            {
                string[] lines = ["We decided to review the release.", "Morgan will send the checklist on Friday.", "Who will verify the results?", "We did not decide to ship.", "Maybe we will deliver tomorrow.", "An additional topic."];
                for (var i = 0; i < lines.Length; i++) log.Append(new(id, "Final", AudioSource.System, i, i + 1, lines[i], Guid.NewGuid(), "s" + i));
            }
            return TranscriptDetails.Read(dir);
        }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { return; } throw new Exception("Unsafe outcome edit accepted"); }
        test("Outcome candidates quote sources, abstain on negation and never draft unreviewed items", () =>
        {
            var dir = folder("outcome-candidates"); var source = Seed(dir); var review = new MeetingOutcomes(dir, source);
            check(review.Items.Length == 3 && review.Items.All(i => i.Choice == OutcomeChoice.Pending) && review.Draft() == "", "Unreviewed or uncertain candidates entered draft");
            review.Set("s1", OutcomeKind.Commitment, OutcomeChoice.Include); review.Set("s2", OutcomeKind.Question, OutcomeChoice.Resolved);
            var draft = review.Draft(); check(draft.Contains(source[1].Text) && !draft.Contains(source[0].Text) && !draft.Contains(source[2].Text) && !draft.Contains("2026"), "Draft fabricated or included unreviewed facts");
            review.Set("s1", OutcomeKind.Commitment, OutcomeChoice.Pending); check(review.Draft() == "", "Review choices were not reversible");
            review.Set("s5", OutcomeKind.Decision, OutcomeChoice.Include); check(review.Draft().Contains("An additional topic."), "Manual source-linked candidate was lost");
        });
        test("Outcome journal rejects stale writers and changed transcripts while recovering only incomplete tails", () =>
        {
            var dir = folder("outcome-concurrency"); var source = Seed(dir); var original = File.ReadAllBytes(Path.Combine(dir, "transcript.jsonl")); var a = new MeetingOutcomes(dir, source); var b = new MeetingOutcomes(dir, source);
            using (var active = new FileStream(Path.Combine(dir, "recording.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) Reject(() => a.Set("s0", OutcomeKind.Decision, OutcomeChoice.Include));
            a.Set("s0", OutcomeKind.Decision, OutcomeChoice.Include); Reject(() => b.Set("s1", OutcomeKind.Commitment, OutcomeChoice.Include)); Reject(() => b.Draft());
            File.AppendAllText(Path.Combine(dir, MeetingOutcomes.FileName), "{incomplete"); var c = new MeetingOutcomes(dir, source);
            check(c.RecoveredTail && c.Items.Single(i => i.SegmentId == "s0").Choice == OutcomeChoice.Include, "Incomplete tail hid completed choices");
            c.Set("s0", OutcomeKind.Decision, OutcomeChoice.Dismiss);
            check(!c.RecoveredTail && original.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "transcript.jsonl"))), "Outcome review rewrote original transcript");
            File.AppendAllText(Path.Combine(dir, MeetingOutcomes.FileName), "broken\n"); Reject(() => new MeetingOutcomes(dir, source));
            File.AppendAllText(Path.Combine(dir, "transcript.jsonl"), " "); Reject(() => a.Draft());
        });
        test("Backup restoration retains outcome choices and original transcript provenance", () =>
        {
            var root = folder("outcome-backup"); var data = Path.Combine(root, "data"); var dir = Path.Combine(data, "recordings", "fixture"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
            var source = Seed(dir); new MeetingOutcomes(dir, source).Set("s1", OutcomeKind.Commitment, OutcomeChoice.Include);
            var vault = Path.Combine(root, "Vault"); Directory.CreateDirectory(vault); File.WriteAllText(Path.Combine(vault, "Home.md"), "# Fixture"); var exe = Path.Combine(root, "SecondBrain.exe"); File.WriteAllText(exe, "fixture");
            var backup = LocalBackup.Create(data, vault, exe, Path.Combine(root, "backups")); var restored = LocalBackup.Restore(backup, Path.Combine(root, "restored"));
            var saved = Path.Combine(restored, "data/recordings/fixture");
            check(new MeetingOutcomes(saved, TranscriptDetails.Read(saved)).Draft().Contains(source[1].Text), "Restored review lost choices or source binding");
        });
    }
}
