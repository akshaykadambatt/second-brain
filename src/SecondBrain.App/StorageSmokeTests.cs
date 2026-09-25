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
        var importSource = Path.Combine(directory, "Backup source.txt"); File.WriteAllText(importSource, "Preserved document fixture.");
        var imported = (await main.ImportDocuments([importSource], "Cedar")).Single();
        check(imported.State == "Imported", "A source document is imported before the backup");
        var pdfSource = Path.Combine(directory, "Backup source.pdf"); var pdfBytes = PdfImportSmokeTests.Fixture("Preserved PDF evidence."); File.WriteAllBytes(pdfSource, pdfBytes);
        var importedPdf = (await main.ImportDocuments([pdfSource], "Cedar")).Single();
        check(importedPdf.State == "Imported", "A PDF document is imported before backup");
        var wordSource = Path.Combine(directory, "Backup.docx"); var wordBytes = OfficeImportSmokeTests.WordFixture(); File.WriteAllBytes(wordSource, wordBytes);
        var importedWord = (await main.ImportDocuments([wordSource], "Cedar")).Single(); check(importedWord.State == "Imported", "Word source imported before backup");
        var deckSource = Path.Combine(directory, "Backup.pptx"); var deckBytes = OfficeImportSmokeTests.PresentationFixture(); File.WriteAllBytes(deckSource, deckBytes);
        var importedDeck = (await main.ImportDocuments([deckSource], "Cedar")).Single(); check(importedDeck.State == "Imported", "Presentation imported before backup");
        var backup = await main.StorageOperation(root => LocalBackup.Create(directory, root, Environment.ProcessPath!, backupParent));
        check(backup is not null, "Packaged UI quiesces writers and creates a verified snapshot");
        var manifest = LocalBackup.Verify(backup!);
        check(manifest.Files.Any(f => f.Path.Contains("history.git/objects/")) && manifest.Files.Any(f => f.Path.EndsWith("session.json")), "Snapshot contains the private Git repository and saved recording");
        var restored = await main.StorageOperation(_ => LocalBackup.Restore(backup!, backupParent));
        check(restored is not null && File.Exists(Path.Combine(restored, "SecondBrain.exe")) && new VaultSettings(Path.Combine(restored, "data"), restored).Resolve(new("Vault")) == Path.Combine(restored, "Vault"), "Restore creates an adjacent EXE, data and portable vault");
        var restoredImport = DocumentImports.List(Path.Combine(restored!, "Vault")).Single(r => r.Document!.Id == imported.Document!.Id).Document!;
        check(restoredImport.Sha256 == imported.Document!.Sha256 && File.ReadAllBytes(VaultFiles.SafePath(Path.Combine(restored!, "Vault"), restoredImport.Original)).SequenceEqual(File.ReadAllBytes(importSource)), "Backup restore retains original document bytes, searchable note and metadata");
        check(File.ReadAllBytes(VaultFiles.SafePath(Path.Combine(restored!, "Vault"), importedWord.Document!.Original)).SequenceEqual(wordBytes), "Backup restores original Word bytes and metadata");
        check(File.ReadAllBytes(VaultFiles.SafePath(Path.Combine(restored!, "Vault"), importedDeck.Document!.Original)).SequenceEqual(deckBytes), "Backup restores original presentation bytes");
        var restoredPdf = DocumentImports.List(Path.Combine(restored!, "Vault")).Single(r => r.Document!.Id == importedPdf.Document!.Id).Document!;
        check(File.ReadAllBytes(VaultFiles.SafePath(Path.Combine(restored!, "Vault"), restoredPdf.Original)).SequenceEqual(pdfBytes) && File.ReadAllText(VaultFiles.SafePath(Path.Combine(restored!, "Vault"), restoredPdf.Note)).Contains("source page 1"), "Backup restores PDF originals and page-linked searchable notes");
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
