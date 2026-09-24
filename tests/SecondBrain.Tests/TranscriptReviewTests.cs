using SecondBrain.Core;

internal static class TranscriptReviewTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        TranscriptDetail[] Fixture() => [new(1, Guid.NewGuid(), "one", AudioSource.System, Guid.NewGuid(), 0, 4, "Alpha beta gamma delta.",
            [new("a", "Alpha", 0, 1, SpeakerId: "s1", SpeakerLabel: "Speaker 1"), new("b", "beta", 1, 2, SpeakerId: "s1", SpeakerLabel: "Speaker 1"),
             new("c", "gamma", 2, 3, SpeakerId: "s2", SpeakerLabel: "Speaker 2"), new("d", "delta.", 3, 4, SpeakerId: "s2", SpeakerLabel: "Speaker 2")])];
        test("Speaker rename split merge and undo retain original word provenance", () =>
        {
            var path = folder("review-edit"); var records = Fixture(); var original = System.Text.Json.JsonSerializer.Serialize(records);
            var review = new TranscriptReview(path, records); check(review.Turns().Length == 2, "Speaker turns not grouped");
            review.Rename("s1", "Morgan"); review.Assign(["b"], "Taylor");
            check(review.Turns().Length == 3 && review.Words[1].Corrected, "Split did not create separate speaker turn");
            review.Merge("s2", "s1"); check(review.Words[^1].Speaker == "Morgan", "Merge lost destination speaker");
            review.Undo(); check(review.Words[^1].SpeakerId == "s2", "Undo merge failed");
            review.Undo(); check(review.Words[1].Speaker == "Morgan", "Undo split failed");
            var reopened = new TranscriptReview(path, records);
            check(reopened.Words[0].Speaker == "Morgan" && reopened.Words[2].Speaker == "Speaker 2" && original == System.Text.Json.JsonSerializer.Serialize(records), "Reload changed original metadata or lost corrections");
            reopened.Undo(); check(!reopened.CanUndo && !reopened.Words.Any(w => w.Corrected), "Undo rename failed");
        });
        test("Review search bookmarks and legacy segment correction survive reopening", () =>
        {
            var path = folder("review-legacy"); var records = Fixture(); records = [records[0] with { Words = [] }];
            var review = new TranscriptReview(path, records); var id = review.Words[0].Id;
            review.Assign([id], "Morgan"); review.Bookmark(id);
            var opened = new TranscriptReview(path, records);
            check(opened.Turns("GAMMA", true).Length == 1 && opened.Turns("missing").Length == 0 && !opened.Words[0].Timed, "Legacy review fabricated timing or lost bookmark/search");
            opened.Bookmark(id); check(opened.Turns(bookmarksOnly: true).Length == 0, "Bookmark removal failed");
            opened.Undo(); check(opened.Turns(bookmarksOnly: true).Length == 1, "Bookmark undo failed");
        });
        test("Review rejects stale writers and changed source provenance", () =>
        {
            var path = folder("review-stale"); var records = Fixture(); var first = new TranscriptReview(path, records); var stale = new TranscriptReview(path, records);
            first.Rename("s1", "Morgan");
            try { stale.Rename("s1", "Wrong"); throw new Exception("Stale edit accepted"); } catch (IOException) { }
            var file = Path.Combine(path, TranscriptReview.FileName); var before = File.ReadAllBytes(file);
            try { first.Assign(["outside"], "Wrong"); throw new Exception("Unbound word accepted"); } catch (InvalidDataException) { }
            var changed = records[0] with { Words = records[0].Words.Select(w => w with { Text = "Replaced" }).ToArray() };
            try { _ = new TranscriptReview(path, [changed]); throw new Exception("Changed source accepted"); } catch (InvalidDataException) { }
            check(before.SequenceEqual(File.ReadAllBytes(file)), "Rejected edit changed the journal");
        });
        test("Review recovers interrupted tails but refuses complete corruption", () =>
        {
            var path = folder("review-recovery"); var records = Fixture(); var review = new TranscriptReview(path, records); review.Rename("s1", "Morgan");
            var file = Path.Combine(path, TranscriptReview.FileName); File.AppendAllText(file, "{interrupted");
            review = new(path, records); check(review.RecoveredTail && review.Words[0].Speaker == "Morgan", "Completed correction was lost");
            review.Bookmark("a"); check(new TranscriptReview(path, records).Bookmarks.Contains("a"), "Append after recovery failed");
            File.AppendAllText(file, "broken\n"); var bytes = File.ReadAllBytes(file);
            try { _ = new TranscriptReview(path, records); throw new Exception("Corruption ignored"); } catch (System.Text.Json.JsonException) { }
            check(bytes.SequenceEqual(File.ReadAllBytes(file)), "Corrupt journal overwritten");
        });
    }
}
