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
        var backupParent = Path.Combine(Path.GetDirectoryName(directory)!, "storage-snapshots");
        var backup = await main.StorageOperation(root => LocalBackup.Create(directory, root, Environment.ProcessPath!, backupParent));
        check(backup is not null, "Packaged UI quiesces writers and creates a verified snapshot");
        var manifest = LocalBackup.Verify(backup!);
        check(manifest.Files.Any(f => f.Path.Contains("history.git/objects/")) && manifest.Files.Any(f => f.Path.EndsWith("session.json")), "Snapshot contains the private Git repository and saved recording");
        var restored = await main.StorageOperation(_ => LocalBackup.Restore(backup!, backupParent));
        check(restored is not null && File.Exists(Path.Combine(restored, "SecondBrain.exe")) && new VaultSettings(Path.Combine(restored, "data"), restored).Resolve(new("Vault")) == Path.Combine(restored, "Vault"), "Restore creates an adjacent EXE, data and portable vault");
        var deleted = await main.StorageOperation(_ => { LocalBackup.DeleteRecording(directory, recording); return "Fixture recording deleted"; });
        check(deleted is not null && !Directory.Exists(recording) && File.Exists(Path.Combine(restored!, "data/recordings", Path.GetFileName(recording), "session.json")), "Deletion removes only the selected source recording; the restored copy remains");
        await main.RefreshStorage();
        check(main.Knowledge is not null && main.WorkspaceGrid.IsEnabled && main.StorageUsage.Text.Contains("Total:"), "Storage operations reconnect knowledge and leave controls usable with measured usage");
        // A second process launches this restored EXE after the smoke runner exits.
        File.WriteAllText(Path.Combine(directory, "restore-location.json"), JsonSerializer.Serialize(new { restored, backup, networkUsed = false, physicalDevicesOpened = false }));
        main.StorageMessage.Text = "Verified fixture backup and restore passed. Original installation remains unchanged.";
        capture(main, Path.Combine(directory, "storage.png"));
    }
}
