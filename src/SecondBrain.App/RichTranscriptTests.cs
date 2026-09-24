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
        await Review(main, directory, check, capture);
    }
    private static async Task Review(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var path = Path.Combine(directory, "review-fixture"); Directory.CreateDirectory(path); var id = Guid.NewGuid();
        var entry = new TranscriptEntry(id, "Final", AudioSource.System, 1, 3, "Review the milestone.", Guid.NewGuid(), "review");
        using (var journal = new TranscriptLog(path, id)) journal.Append(entry);
        using (var details = new TranscriptDetails(path, id)) details.Append(TranscriptDetails.From(entry, [new("Review", 1, 1.5), new("the", 1.5, 2), new("milestone.", 2, 3)], "Timed"));
        using (var wav = new NAudio.Wave.WaveFileWriter(Path.Combine(path, "system.wav"), new NAudio.Wave.WaveFormat(16000, 16, 1))) wav.Write(new byte[128000], 0, 128000);
        var original = File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"));
        var viewer = (await main.OpenTranscript(path))!;
        void Click(string text) => viewer.EditControls.Children.OfType<System.Windows.Controls.Button>().Single(b => (string)b.Content == text).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        viewer.SpeakerName.Text = "Morgan"; Click("Name this turn");
        check(viewer.Review.Words.All(w => w.Speaker == "Morgan"), "Naming a review turn saves a reversible manual correction");
        viewer.WordSelection.SelectedIndex = 1; viewer.SpeakerName.Text = "Taylor"; Click("Split selected words");
        check(viewer.Segments.Items.Count == 3, "Splitting selected words separates speaker turns");
        viewer.UndoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        viewer.BookmarkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        viewer.Search.Text = "MILESTONE"; check(viewer.Segments.Items.Count == 1, "Transcript search finds text case-insensitively");
        check(Math.Abs(viewer.ReplaySelection() - 1) < .001, "Timestamp replay seeks the correct saved source audio without opening capture");
        capture(viewer, Path.Combine(directory, "transcript-review.png")); viewer.Close();
        viewer = (await main.OpenTranscript(path))!;
        check(viewer.Review.Bookmarks.Count == 1 && viewer.Review.Words[0].Speaker == "Morgan" && original.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"))), "Review bookmarks and corrections reopen without altering source bytes"); viewer.Close();
        File.AppendAllText(Path.Combine(path, TranscriptReview.FileName), "broken\n");
        viewer = (await main.OpenTranscript(path))!;
        check(!viewer.EditControls.IsEnabled && viewer.Summary.Text.Contains("read-only"), "Corrupt correction journal preserves original review in read-only mode"); viewer.Close();
    }
}
