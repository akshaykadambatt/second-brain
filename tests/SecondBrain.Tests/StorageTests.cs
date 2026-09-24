using System.Text.Json;
using SecondBrain.Core;

internal static class StorageTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        (string Data, string Vault, string Exe) Fixture(string root)
        {
            var data = Path.Combine(root, "data"); var vault = Path.Combine(root, "Vault");
            Directory.CreateDirectory(data); Directory.CreateDirectory(vault);
            File.WriteAllText(Path.Combine(root, "SecondBrain.exe"), "fixture executable");
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"ReaderStyle\":{\"FontSize\":36}}");
            File.WriteAllText(Path.Combine(data, "vault-settings.json"), JsonSerializer.Serialize(new { Folder = vault, Semantic = false, AutomaticUpdates = false }));
            File.WriteAllBytes(Path.Combine(data, "openai-key.protected"), [1, 3, 5, 7]);
            VaultFiles.Create(vault, "Context/Terminology.md", "# Terms\nManual note — preserved.");
            VaultFiles.Create(vault, "logs/My note.md", "User notes in a folder called logs must be retained.");
            VaultFiles.Create(vault, "Attachments/example.lock", "A user attachment, not a runtime lock.");
            return (data, vault, Path.Combine(root, "SecondBrain.exe"));
        }
        void Refuse(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return; } throw new Exception("Unsafe operation was accepted"); }
        test("Verified backup and restore preserve settings, recordings, transcript and real Git history", () =>
        {
            var source = Fixture(folder("storage-source")); var destination = folder("storage-backups");
            var engine = new VaultMaintenance(source.Vault);
            using (engine.Acquire()) engine.Initialize();
            var head = engine.Git.Head;
            string recording;
            using (var session = new RecordingSession(Path.Combine(source.Data, "recordings"), [new(AudioSource.Microphone, "m", "m", 16000), new(AudioSource.System, "s", "s", 16000)]))
            {
                session.Write(new(AudioSource.Microphone, 0, new byte[32000])); session.Complete(1); recording = session.DirectoryPath;
                using var transcript = new TranscriptLog(recording, session.Manifest.Id); transcript.Append(new(session.Manifest.Id, "Final", AudioSource.System, 0, 1, "A saved fixture transcript."));
            }
            // Runtime locks may be held and are deliberately excluded from the snapshot.
            using var instance = new FileStream(Path.Combine(source.Data, "instance.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var backup = LocalBackup.Create(source.Data, source.Vault, source.Exe, destination);
            var manifest = LocalBackup.Verify(backup);
            check(manifest.Files.Any(f => f.Path.EndsWith("transcript.jsonl")) && manifest.Files.Any(f => f.Path.EndsWith("microphone.wav")) && !manifest.Files.Any(f => f.Path.EndsWith("instance.lock")), "Backup missing durable content or copied runtime locks");
            check(manifest.Files.Any(f => f.Path == "Vault/logs/My note.md") && manifest.Files.Any(f => f.Path == "Vault/Attachments/example.lock"), "User vault files were mistaken for app logs/locks");
            var restored = LocalBackup.Restore(backup, destination);
            var restoredVault = Path.Combine(restored, "Vault");
            check(new VaultGit(restoredVault).Head == head && new VaultGit(restoredVault).Log().Contains("manual", StringComparison.OrdinalIgnoreCase), "Git history not portable");
            foreach (var file in manifest.Files.Where(f => f.Path != "data/vault-settings.json"))
                check(File.ReadAllBytes(Path.Combine(restored, file.Path)).SequenceEqual(File.ReadAllBytes(Path.Combine(backup, file.Path))), "Restored bytes differ: " + file.Path);
            using var options = JsonDocument.Parse(File.ReadAllText(Path.Combine(restored, "data/vault-settings.json")));
            check(options.RootElement.GetProperty("Folder").GetString() == "Vault" && !options.RootElement.GetProperty("Semantic").GetBoolean(), "Restored vault not remapped or settings changed");
            check(LocalBackup.Measure(source.Data, source.Vault).Sum(s => s.Bytes) > 32000, "Storage usage missing audio");
            var backupBefore = File.ReadAllText(Path.Combine(backup, "secondbrain-backup.json"));
            LocalBackup.DeleteRecording(source.Data, recording);
            check(!Directory.Exists(recording) && Directory.Exists(restoredVault) && File.ReadAllText(Path.Combine(backup, "secondbrain-backup.json")) == backupBefore, "Deletion affected backup/vault");
            LocalBackup.Verify(backup);
        });
        test("Restored interrupted audio, transcript and Markdown transaction recover without altering originals", () =>
        {
            var source = Fixture(folder("storage-interrupted")); var parent = folder("storage-interrupted-copies");
            string recording;
            using (var session = new RecordingSession(Path.Combine(source.Data, "recordings"), [new(AudioSource.Microphone, "m", "m", 16000), new(AudioSource.System, "s", "s", 16000)]))
            {
                recording = session.DirectoryPath; session.Write(new(AudioSource.Microphone, 0, new byte[32000]));
                using var transcript = new TranscriptLog(recording, session.Manifest.Id);
                transcript.Append(new(session.Manifest.Id, "RunStart", null, 0, 0, "fixture"));
                transcript.Append(new(session.Manifest.Id, "Final", AudioSource.Microphone, 0, .5, "Preserved words"));
            }
            var engine = new VaultMaintenance(source.Vault);
            using (engine.Acquire())
            {
                engine.Initialize(); var before = engine.Capture();
                var transaction = new VaultTransaction(engine.Git);
                try { transaction.Apply(before, [new("Context/Terminology.md", before["Context/Terminology.md"], System.Text.Encoding.UTF8.GetBytes("Restored committed update"))], "fixture update", "fixture-op", _ => throw new SimulatedVaultCrashException()); }
                catch (SimulatedVaultCrashException) { }
                check(transaction.Pending, "Crash did not preserve its journal");
            }
            var backup = LocalBackup.Create(source.Data, source.Vault, source.Exe, parent); var restored = LocalBackup.Restore(backup, parent);
            var restoredData = Path.Combine(restored, "data"); var restoredRecording = Path.Combine(restoredData, "recordings", Path.GetFileName(recording));
            LocalBackup.RecoverRecording(restoredData, restoredRecording); LocalBackup.RecoverRecording(restoredData, restoredRecording);
            var entries = TranscriptLog.Read(restoredRecording);
            check(RecordingSession.ReadManifest(restoredRecording).State == "Recovered" && entries.Count(e => e.Kind == "Final") == 1 && entries.Count(e => e.Kind == "Gap") == 2, "Recovered transcript missing, duplicated or without gaps");
            var restoredEngine = new VaultMaintenance(Path.Combine(restored, "Vault"));
            using (restoredEngine.Acquire()) restoredEngine.Initialize();
            check(!new VaultTransaction(restoredEngine.Git).Pending && restoredEngine.Git.Diff(restoredEngine.Git.Head).Contains("Restored committed update"), "Restored journal/Git recovery failed");
            check(RecordingSession.ReadManifest(recording).State == "Recording" && new VaultTransaction(engine.Git).Pending, "Recovery changed original installation");
            LocalBackup.Verify(backup);
        });
        test("Restore rejects damaged, incomplete, duplicate and escaping backup paths", () =>
        {
            var source = Fixture(folder("storage-invalid-source")); var parent = folder("storage-invalid-backups");
            var backup = LocalBackup.Create(source.Data, source.Vault, source.Exe, parent);
            var manifestPath = Path.Combine(backup, "secondbrain-backup.json"); var original = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(original)!;
            File.WriteAllText(Path.Combine(backup, "Vault/Context/Terminology.md"), "damaged");
            Refuse(() => LocalBackup.Restore(backup, parent));
            File.Copy(Path.Combine(source.Vault, "Context/Terminology.md"), Path.Combine(backup, "Vault/Context/Terminology.md"), true);
            foreach (var path in new[] { "../outside.txt", "Vault/../../outside.txt", "Vault/file:stream", "Vault/./alias", "Vault/file." })
            {
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { Files = [.. manifest.Files, new(path, 0, "")] }));
                Refuse(() => LocalBackup.Restore(backup, parent));
            }
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { Files = [.. manifest.Files, manifest.Files[0]] })); Refuse(() => LocalBackup.Verify(backup));
            File.WriteAllText(manifestPath, original); File.WriteAllText(Path.Combine(backup, "unexpected.txt"), "unlisted"); Refuse(() => LocalBackup.Verify(backup));
            Refuse(() => LocalBackup.Restore(folder("incomplete.partial"), parent));
            check(!Directory.GetDirectories(parent, "SecondBrain-restored-*").Any(), "Failed validation published a restored folder");
        });
        test("Storage deletion refuses active sessions, outside folders and nested paths", () =>
        {
            var root = folder("storage-delete"); var data = Path.Combine(root, "data");
            using var active = new RecordingSession(Path.Combine(data, "recordings"), [new(AudioSource.Microphone, "m", "m", 16000), new(AudioSource.System, "s", "s", 16000)]);
            Refuse(() => LocalBackup.DeleteRecording(data, active.DirectoryPath));
            active.Complete(0); Refuse(() => LocalBackup.DeleteRecording(data, active.DirectoryPath));
            Refuse(() => LocalBackup.DeleteRecording(data, root));
            Refuse(() => LocalBackup.DeleteRecording(data, Path.Combine(active.DirectoryPath, "child")));
            check(File.Exists(Path.Combine(active.DirectoryPath, "session.json")), "Refusal modified session");
        });
        test("Backup refuses linked folders, locked files and recursive destinations without touching source", () =>
        {
            var root = folder("storage-boundaries"); var source = Fixture(root); var parent = folder("storage-boundary-backups");
            Refuse(() => LocalBackup.Create(source.Data, source.Vault, source.Exe, Path.Combine(source.Vault, "Backups")));
            using (var locked = new FileStream(Path.Combine(source.Data, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Refuse(() => LocalBackup.Create(source.Data, source.Vault, source.Exe, parent));
            check(!Directory.GetDirectories(parent).Any(p => !p.EndsWith(".partial")), "Incomplete backup published as complete");
            var link = Path.Combine(source.Vault, "linked");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + source.Data.Replace("'", "''") + "' | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
            check(process.ExitCode == 0, "Could not create junction fixture: " + process.StandardError.ReadToEnd());
            try { Refuse(() => LocalBackup.Create(source.Data, source.Vault, source.Exe, parent)); }
            finally { Directory.Delete(link); }
        });
    }
}
