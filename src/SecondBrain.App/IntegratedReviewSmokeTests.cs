using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class IntegratedReviewSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var data = Path.Combine(directory, "portable-data"); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        var recordings = Path.Combine(data, "recordings"); Directory.CreateDirectory(recordings); var path = MeetingReviewSmokeTests.Fixture(recordings, "m");
        var original = File.ReadAllBytes(Path.Combine(path, "transcript.jsonl")); var records = TranscriptDetails.Read(path);
        var review = new TranscriptReview(path, records); review.Assign([review.Words[0].Id], "Fixture reviewer"); review.Bookmark(review.Words[0].Id);
        new MeetingOutcomes(path, records).Set("commitment", OutcomeKind.Commitment, OutcomeChoice.Include);
        var clip = Path.Combine(path, "video-interrupted.mp4");
        await Task.Factory.StartNew(() =>
        {
            using (var writer = new NativeVideoWriter(clip, 320, 180))
            { var frame = new byte[320 * 180 * 4]; for (var i = 0; i < 60; i++) writer.Write(frame, i); writer.Complete(); }
            using var tail = new FileStream(clip, FileMode.Append); tail.Write([0, 0, 0]);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        File.WriteAllText(Path.ChangeExtension(clip, ".json"), "{\"SessionStartSeconds\":0,\"State\":\"Recording\"}");
        var recovered = VideoRecovery.Recover(path, Path.GetFileName(clip));
        var vault = Path.Combine(directory, "portable-vault"); Directory.CreateDirectory(vault); File.WriteAllText(Path.Combine(vault, "Home.md"), "# Synthetic portable vault");
        var backup = await Task.Run(() => LocalBackup.Create(data, vault, Environment.ProcessPath!, Path.Combine(directory, "backups")));
        var restored = await Task.Run(() => LocalBackup.Restore(backup, Path.Combine(directory, "r")));
        var restoredMeeting = Path.Combine(restored, "data/recordings/m");
        check(File.ReadAllBytes(Path.Combine(restoredMeeting, "transcript.jsonl")).SequenceEqual(original), "Integrated restoration preserves immutable transcript bytes");
        var restoredReview = new TranscriptReview(restoredMeeting, TranscriptDetails.Read(restoredMeeting));
        check(restoredReview.Words[0].Speaker == "Fixture reviewer" && restoredReview.Bookmarks.Count == 1, "Restoration retains additive speaker corrections and bookmarks");
        check(MeetingArchive.Read(restoredMeeting)!.Answers[0].Sources[0].File == "Notes/release.md"
            && new MeetingOutcomes(restoredMeeting, TranscriptDetails.Read(restoredMeeting)).Draft().Contains("Morgan will send"), "Restoration retains scoped answer evidence and included follow-up choices");
        var restoredClip = Path.Combine(restoredMeeting, Path.GetFileName(recovered.Path));
        check((await Task.Run(() => VideoSmokeTests.Decode(restoredClip))).Count == 60, "Recovered video remains decodable after complete backup and restore");
        var window = (await main.OpenTranscript(restoredMeeting))!;
        try
        {
            window.ReviewTabs.SelectedItem = window.OutcomesTab; window.Outcomes.BuildDraft(); window.UpdateLayout();
            check(window.Outcomes.DraftText.Text.Contains("Morgan will send") && window.Video.Library.Clips.Items.Count == 2 && !main.Recorder.HasSession, "Restored meeting opens as one review with both video originals and recovered copies without capture");
            capture(window, Path.Combine(directory, "restored-meeting-review.png"));
        }
        finally { window.Close(); }
    }
}
