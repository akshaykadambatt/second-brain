using System.IO;
using System.Text.Json;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class StorageSmokeTests
{
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.SettingsTab.IsSelected = true; main.StorageTab.IsSelected = true;
        await main.Recorder.StartAsync("test-mic", "test-output"); await Task.Delay(250);
        var invoked = false;
        var refused = await main.StorageOperation(_ => { invoked = true; return "wrong"; });
        check(refused is null && !invoked && main.Recorder.HasSession, "Storage operation refuses an active recording without stopping or deleting it");
        await main.Recorder.StopAsync();
        var recording = main.Recorder.LastDirectory!;
        var contextStore = new SessionContextStore(directory);
        var brief = new SessionContext { ProfileId = Guid.NewGuid(), Client = "Cedar", Goal = "Fixture backup" };
        contextStore.SaveProfile(contextStore.Load(), brief);
        SessionContextStore.SaveSnapshot(recording, RecordingSession.ReadManifest(recording).Id, brief);
        LatencyReport.Create([]).Save(Path.Combine(recording, "latency.json"));
        var transcriptId = RecordingSession.ReadManifest(recording).Id;
        var detailEntry = new TranscriptEntry(transcriptId, "Final", AudioSource.System, 0, .2, "Saved.", Guid.NewGuid(), "backup-word-fixture");
        using (var transcript = new TranscriptLog(recording, transcriptId)) transcript.Append(detailEntry);
        new SpeakerSettings(directory).Save(new(true, "Morgan"));
        using (var details = new TranscriptDetails(recording, transcriptId)) details.Append(new AudioSpeakers(transcriptId, new()).Label(TranscriptDetails.From(detailEntry, [new("Saved.", 0, .2, .9f, 0)], "Word timing available")));
        var hinted = TranscriptDetails.Read(recording).Single();
        new SpeakerNameHints(recording, transcriptId).Append(hinted, new(1, transcriptId, hinted.SegmentId, SpeakerNameHints.Binding(hinted), "Screen fixture", 0, .2));
        var backupParent = Path.Combine(Path.GetDirectoryName(directory)!, "storage-snapshots");
        var review = new TranscriptReview(recording, TranscriptDetails.Read(recording)); review.Assign([review.Words[0].Id], "Taylor"); review.Bookmark(review.Words[0].Id);
        var backup = await main.StorageOperation(root => LocalBackup.Create(directory, root, Environment.ProcessPath!, backupParent));
        check(backup is not null, "Packaged UI quiesces writers and creates a verified snapshot");
        var manifest = LocalBackup.Verify(backup!);
        check(manifest.Files.Any(f => f.Path.Contains("history.git/objects/")) && manifest.Files.Any(f => f.Path.EndsWith("session.json")), "Snapshot contains the private Git repository and saved recording");
        var restored = await main.StorageOperation(_ => LocalBackup.Restore(backup!, backupParent));
        check(restored is not null && File.Exists(Path.Combine(restored, "SecondBrain.exe")) && new VaultSettings(Path.Combine(restored, "data"), restored).Resolve(new("Vault")) == Path.Combine(restored, "Vault"), "Restore creates an adjacent EXE, data and portable vault");
        check(new SessionContextStore(Path.Combine(restored!, "data")).Load().SelectedProfileId == brief.ProfileId
            && SessionContextStore.ReadSnapshot(Path.Combine(restored!, "data/recordings", Path.GetFileName(recording)))?.Context.Goal == brief.Goal,
            "Backup and restore retain client profiles and per-meeting context snapshots");
        check(File.ReadAllText(Path.Combine(restored!, "data/recordings", Path.GetFileName(recording), "latency.json")) == File.ReadAllText(Path.Combine(recording, "latency.json")),
            "Backup and restore retain meeting latency reports");
        check(TranscriptDetails.Read(Path.Combine(restored!, "data/recordings", Path.GetFileName(recording))).Single().Words.Single().Id == "backup-word-fixture:w0",
            "Backup and restore retain word sidecars and their source-segment identities");
        check(new SpeakerSettings(Path.Combine(restored!, "data")).Load().LocalParticipant == "Morgan"
            && TranscriptDetails.Read(Path.Combine(restored!, "data/recordings", Path.GetFileName(recording))).Single().Words.Single().SpeakerLabel == "Speaker 1",
            "Backup and restore retain microphone preferences and session speaker labels");
        var deleted = await main.StorageOperation(_ => { LocalBackup.DeleteRecording(directory, recording); return "Fixture recording deleted"; });
        var restoredRecording = Path.Combine(restored!, "data/recordings", Path.GetFileName(recording));
        var restoredReview = new TranscriptReview(restoredRecording, TranscriptDetails.Read(restoredRecording));
        check(restoredReview.Words[0].Speaker == "Taylor" && restoredReview.Bookmarks.Count == 1 && restoredReview.CanUndo, "Backup restore retains reversible review corrections and bookmarks");
        check(SpeakerNameHints.Read(restoredRecording, TranscriptDetails.Read(restoredRecording), out var hintWarning).Count == 1 && hintWarning.Length == 0, "Backup restores source-bound screen-name hints with manual correction priority intact");
        check(deleted is not null && !Directory.Exists(recording) && File.Exists(Path.Combine(restored!, "data/recordings", Path.GetFileName(recording), "session.json")), "Deletion removes only the selected source recording; the restored copy remains");
        await main.RefreshStorage();
        check(main.Knowledge is not null && main.WorkspaceGrid.IsEnabled && main.StorageUsage.Text.Contains("Total:"), "Storage operations reconnect knowledge and leave controls usable with measured usage");
        // A second process launches this restored EXE after the smoke runner exits.
        File.WriteAllText(Path.Combine(directory, "restore-location.json"), JsonSerializer.Serialize(new { restored, backup, networkUsed = false, physicalDevicesOpened = false }));
        main.StorageMessage.Text = "Verified fixture backup and restore passed. Original installation remains unchanged.";
        capture(main, Path.Combine(directory, "storage.png"));
    }
}
