using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class RichTranscriptTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var path = Path.Combine(directory, "transcript-fixture"); Directory.CreateDirectory(path); var id = Guid.NewGuid();
        var entry = new TranscriptEntry(id, "Final", AudioSource.System, 1, 2, "Confirmed milestone.", Guid.NewGuid(), "stable");
        using (var journal = new TranscriptLog(path, id)) journal.Append(entry);
        using (var detail = new TranscriptDetails(path, id)) detail.Append(TranscriptDetails.From(entry, [new("Confirmed", 1, 1.4, .9f), new("milestone.", 1.5, 2, .9f)], "Word timing available"));
        var before = File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"));
        var viewer = await main.OpenTranscript(path);
        check(viewer is not null && viewer.Segments.Items.Count == 1 && viewer.WordDetails.Text.Contains("1.000–1.400s") && viewer.Summary.Text.Contains("2 timed words"),
            "Meeting transcript viewer exposes mapped word times alongside original segments");
        capture(viewer!, Path.Combine(directory, "transcript-details.png")); viewer!.Close();
        File.WriteAllText(Path.Combine(path, TranscriptDetails.FileName), "broken metadata\n");
        viewer = await main.OpenTranscript(path);
        check(viewer is not null && viewer.Summary.Text.Contains("showing original") && viewer.WordDetails.Text.Contains("unavailable"), "Damaged optional metadata falls back visibly to the original transcript"); viewer!.Close();
        File.Delete(Path.Combine(path, TranscriptDetails.FileName));
        viewer = await main.OpenTranscript(path);
        check(viewer is not null && viewer.Segments.Items.Count == 1 && viewer.Summary.Text.Contains("0 timed words"), "Legacy recordings open without fabricated word timing"); viewer!.Close();
        check(File.ReadAllBytes(Path.Combine(path, "transcript.jsonl")).SequenceEqual(before) && !main.Recorder.HasSession, "Review leaves original transcript bytes intact and never starts capture");
    }
}
