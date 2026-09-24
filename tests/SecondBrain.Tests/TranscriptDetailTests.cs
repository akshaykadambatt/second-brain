using SecondBrain.Core;

internal static class TranscriptDetailTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Provider words retain punctuation, timestamps and optional speaker metadata", () =>
        {
            var segment = DeepgramProtocol.Parse("""{"type":"Results","start":1,"duration":2,"is_final":true,"channel":{"alternatives":[{"transcript":"Hello world.","confidence":0.9,"words":[{"word":"hello","punctuated_word":"Hello","start":1.1,"end":1.4,"confidence":0.9,"speaker":0},{"word":"world","punctuated_word":"world.","start":1.5,"end":2,"confidence":0.8,"speaker":1,"speaker_confidence":0.75}]}]}}""")!;
            check(segment.Words.Length == 2 && segment.Words[1].Text == "world." && segment.Words[1].Speaker == 1 && segment.Words[0].SpeakerConfidence is null && segment.LastWordEnd == 2,
                "Word metadata or absent confidence was lost");
            var invalid = DeepgramProtocol.Parse("""{"type":"Results","start":0,"duration":1,"is_final":true,"channel":{"alternatives":[{"transcript":"Original retained.","confidence":0.9,"words":[{"word":"bad","start":"wrong","end":1},{"word":"outside","start":2,"end":3}]}]}}""")!;
            check(invalid.Text == "Original retained." && invalid.Words.Length == 0 && invalid.WordTimingStatus.Contains("invalid"), "Invalid optional words destroyed original text or invented timing");
        });
        test("Word detail sidecars preserve stable IDs and original transcript bytes", () =>
        {
            var path = folder("word-details"); var id = Guid.NewGuid(); var entry = new TranscriptEntry(id, "Final", AudioSource.System, 1, 2, "Hello.", Guid.NewGuid(), "stable-segment");
            using (var journal = new TranscriptLog(path, id)) journal.Append(entry);
            var before = File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"));
            var detail = TranscriptDetails.From(entry, [new("Hello.", 1.1, 1.5, .9f, 0)], "Word timing available");
            using (var words = new TranscriptDetails(path, id))
            {
                check(words.Append(detail) && !words.Append(detail), "Duplicate word detail was appended");
                try { words.Append(detail with { Text = "Changed" }); throw new Exception("Conflicting identity accepted"); } catch (InvalidDataException) { }
            }
            var read = TranscriptDetails.Read(path).Single();
            check(read.SegmentId == entry.Id && read.Words.Single().Id == "stable-segment:w0" && read.Words[0].Start == 1.1
                && File.ReadAllBytes(Path.Combine(path, "transcript.jsonl")).SequenceEqual(before), "Sidecar lost identity or rewrote the original");
        });
        test("Legacy transcript reads never invent word timing and reject mismatched sidecars", () =>
        {
            var path = folder("legacy-words"); var id = Guid.NewGuid();
            using (var journal = new TranscriptLog(path, id)) journal.Append(new(id, "Final", AudioSource.Microphone, 0, 1, "Legacy text."));
            var first = TranscriptDetails.Read(path).Single(); var again = TranscriptDetails.Read(path).Single();
            check(first.Words.Length == 0 && first.SegmentId == again.SegmentId && first.MetadataStatus.Contains("unavailable"), "Legacy timing/identity was fabricated");
            using (var words = new TranscriptDetails(path, id)) words.Append(first with { Text = "Wrong original" });
            try { TranscriptDetails.Read(path); throw new Exception("Mismatched text accepted"); } catch (InvalidDataException) { }
        });
        test("Interrupted word-detail tails recover while complete corruption stays visible", () =>
        {
            var path = folder("word-recovery"); var id = Guid.NewGuid(); var entry = new TranscriptEntry(id, "Final", AudioSource.System, 0, 1, "Saved.", Id: "saved");
            using (var journal = new TranscriptLog(path, id)) journal.Append(entry);
            using (var words = new TranscriptDetails(path, id)) words.Append(TranscriptDetails.From(entry, [], "No word timing"));
            var file = Path.Combine(path, TranscriptDetails.FileName); var original = File.ReadAllText(file); File.AppendAllText(file, "{partial");
            using (var recovery = new TranscriptDetails(path, id)) { }
            check(File.ReadAllText(file) == original && TranscriptDetails.Read(path).Length == 1, "Partial tail recovery lost a complete record");
            File.AppendAllText(file, "broken complete record\n"); var corrupted = File.ReadAllText(file);
            try { using var refused = new TranscriptDetails(path, id); throw new Exception("Complete corruption ignored"); } catch (System.Text.Json.JsonException) { }
            check(File.ReadAllText(file) == corrupted, "Recovery silently rewrote complete corruption");
        });
    }
}
