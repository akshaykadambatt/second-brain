using System.IO;
using System.Text.Json;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class MeetingReviewSmokeTests
{
    internal static string Fixture(string directory)
    {
        var path = Path.Combine(directory, "review-fixture"); Directory.CreateDirectory(path);
        var manifest = new RecordingManifest { State = "Completed", DurationSeconds = 10, Tracks = [new(AudioSource.Microphone, "test", "Mic", 16000), new(AudioSource.System, "test", "System", 16000)] };
        File.WriteAllText(Path.Combine(path, "session.json"), JsonSerializer.Serialize(manifest));
        SessionContextStore.SaveSnapshot(path, manifest.Id, new());
        using (var log = new TranscriptLog(path, manifest.Id))
        {
            log.Append(new(manifest.Id, "Final", AudioSource.System, 1, 2, "We decided to review the release.", Guid.NewGuid(), "decision"));
            log.Append(new(manifest.Id, "Final", AudioSource.System, 3, 4, "Morgan will send the checklist on Friday.", Guid.NewGuid(), "commitment"));
            log.Append(new(manifest.Id, "Final", AudioSource.System, 5, 6, "Who will verify the results?", Guid.NewGuid(), "question"));
        }
        using (var wav = new NAudio.Wave.WaveFileWriter(Path.Combine(path, "system.wav"), new NAudio.Wave.WaveFormat(16000, 16, 1))) wav.Write(new byte[320000], 0, 320000);
        MeetingArchive.Save(path, [new(Guid.NewGuid(), 1.5, "What was agreed?", "Review the release.", "Complete", [new(Guid.Empty, "Notes/release.md", 4, "The release needs review.")])], false);
        return path;
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var path = Fixture(directory); var original = File.ReadAllBytes(Path.Combine(path, "transcript.jsonl")); var clip = Path.Combine(path, "video-fixture.mp4");
        await Task.Factory.StartNew(() =>
        {
            using var writer = new NativeVideoWriter(clip, 320, 180); var pixels = new byte[320 * 180 * 4]; Array.Fill(pixels, (byte)130);
            for (var i = 0; i < 180; i++) writer.Write(pixels, i); writer.Complete();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        File.WriteAllText(Path.ChangeExtension(clip, ".json"), "{\"State\":\"Completed\",\"SessionStartSeconds\":0.5,\"DurationSeconds\":6,\"Audio\":false}");
        var id = RecordingSession.ReadManifest(path).Id; var receiptPath = VaultFiles.SafePath(main.Knowledge!.Root, $".secondbrain/receipts/{id:N}.json"); Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new MaintenanceReceipt(id, "2026-09-27", "fixture", "test", "Applied", [new("Knowledge/Meeting updates.md", "Source-linked fixture observation.")])));
        var viewer = (await main.OpenTranscript(path))!;
        try
        {
            check(viewer.ReviewTabs.Items.Count >= 4 && viewer.Segments.Items.Count == 3 && viewer.EvidenceText.Text.Contains("Notes/release.md:4") && viewer.ChangesText.Text.Contains("Source-linked fixture"), "Unified meeting review joins transcript, saved answer passages and meeting knowledge receipt");
            viewer.BookmarkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            check(viewer.ReplayVideoSelection() && viewer.Video.RequestedOffset == .5, "Transcript timestamp maps through the saved clip's session offset");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10))) while (!viewer.Video.Opened && viewer.Video.Player is not null) await Task.Delay(20, timeout.Token);
            check(viewer.Video.Opened && viewer.Video.Player!.NaturalVideoWidth == 320, "Windows in-app playback opens native fragmented MP4 and seeks without audio recapture");
            capture(viewer, Path.Combine(directory, "meeting-video.png"));
            viewer.ReviewTabs.SelectedItem = viewer.TranscriptTab; viewer.UpdateLayout();
            check(viewer.ReplaySelection() == 1 && viewer.Video.Player is null, "Audio replay closes video and uses the selected saved source");
            capture(viewer, Path.Combine(directory, "meeting-review.png"));
            viewer.ReviewTabs.SelectedIndex = 2; viewer.UpdateLayout(); capture(viewer, Path.Combine(directory, "meeting-evidence.png"));
            check(!main.Recorder.HasSession && original.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"))), "Unified review never starts capture or rewrites the original transcript");
        }
        finally { viewer.Close(); }
        using (var unlocked = new FileStream(clip, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) check(unlocked.Length > 0, "Closing meeting review releases the video file");
        File.WriteAllText(Path.Combine(path, MeetingArchive.FileName), "broken");
        viewer = (await main.OpenTranscript(path))!;
        check(viewer.Segments.Items.Count == 3 && viewer.EvidenceText.Text.Contains("unavailable") && viewer.Review.Bookmarks.Count == 1, "Damaged optional evidence preserves the original transcript and saved bookmarks"); viewer.Close();
    }
}
