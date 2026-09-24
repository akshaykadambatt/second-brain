using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class MaintenanceTests
{
    private sealed class Provider : IMaintenanceProvider
    {
        public bool Fail { get; set; }
        public bool Hold { get; set; }
        public int Calls { get; private set; }
        public async Task<MaintenanceProposal> Propose(MaintenanceInput input, CancellationToken cancellation)
        {
            Calls++; if (Hold) await Task.Delay(Timeout.Infinite, cancellation);
            if (Fail) throw new IOException("Injected provider outage.");
            var source = input.Evidence[0]; return new([new("Projects/Cedar.md", [new(source.Text, [new(source.Id, source.Text)])])]);
        }
    }
    private static async Task Until(Func<bool> condition, int seconds = 25)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); while (!condition()) await Task.Delay(50, timeout.Token); }
    private static string Source(string root, string directory, string text)
    {
        VaultFiles.Create(root, "Projects/Cedar.md", "# Cedar\n\nExisting human project description.\n");
        using var recording = new RecordingSession(Path.Combine(directory, "synthetic-recordings"), [new(AudioSource.Microphone, "mic", "Fixture", 16000), new(AudioSource.System, "system", "Fixture", 16000)]);
        using (var log = new TranscriptLog(recording.DirectoryPath, recording.Manifest.Id)) log.Append(new(recording.Manifest.Id, "Final", AudioSource.System, 1, 2, text));
        recording.Complete(3); return VaultFiles.ExportMeeting(root, recording.DirectoryPath, "Cedar");
    }
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var root = Path.Combine(directory, "history-vault"); Directory.CreateDirectory(root);
        var summary = Source(root, directory, "The Cedar team agreed the review will be Friday.");
        var provider = new Provider { Fail = true };
        using (var service = new MaintenanceService(root, provider, true))
        {
            await Until(() => service.Status.StartsWith("Update needs attention"));
            check(!File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")).Contains("secondbrain:"), "Provider failure leaves original note intact and reports a retryable job");
            provider.Fail = false; service.Retry();
            await Until(() => service.Status == "Meeting update saved in local history.");
            var view = await service.ReadHistory();
            check(view.Updates.Length == 1 && view.Jobs.Single().State == "Done" && view.Updates[0].Sections.Length == 1, "Retry applies and records a source-linked meeting update");
            service.Queue(summary); await Task.Delay(350); check(provider.Calls == 2, "Duplicate job does not call the provider again");
            provider.Hold = true;
            var next = Source(root, directory, "The owner for the Tuesday review is Sam."); service.Queue(next);
            await Until(() => provider.Calls == 3); await service.Stop();
            check((await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, ".secondbrain/jobs"), "*.json").First(p => File.ReadAllText(p).Contains("Paused on shutdown")))).Contains("Pending"), "Closing during generation leaves a durable pending job");
        }
        using (var resumed = new MaintenanceService(root, new Provider(), true))
        {
            await Until(() => resumed.Status == "Meeting update saved in local history.");
            check((await resumed.ReadHistory()).Updates.Length == 2, "Restart resumes pending generation without duplicating the earlier update"); await resumed.Stop();
        }
        // Show the real history UI against this finished fixture and exercise undo.
        await Until(() => main.VaultApply.IsEnabled);
        main.VaultFolder.Text = root; main.AutoUpdates.IsChecked = true;
        main.VaultApply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.MeetingsTab.IsSelected = true; main.HistoryTab.IsSelected = true;
        await Until(() => main.HistoryUpdates.Items.Count == 2 && main.HistoryRevisions.Items.Count >= 2 && main.HistoryUndo.IsEnabled);
        main.HistoryRevisions.SelectedIndex = 0;
        var history = main.Maintenance!; var viewNow = await history.ReadHistory(); var commit = viewNow.Log.Split(' ')[0];
        main.HistoryDiff.Text = await history.Diff(commit);
        check(main.HistoryDiff.Text.Contains("diff --git") && main.HistoryUpdates.Items.Count == 2, "History screen shows revisions, readable diffs and separately selectable meeting updates");
        capture(main, Path.Combine(directory, "history.png"));
        var target = Path.Combine(root, "Projects/Cedar.md"); File.AppendAllText(target, "\nLater human addition.\n");
        main.HistoryUpdates.SelectedIndex = 0; main.HistoryUndo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => main.HistoryMessage.Text.StartsWith("Update reverted"));
        check(File.ReadAllText(target).Contains("Later human addition") && File.ReadAllText(target).Contains("Existing human project description"), "UI undo preserves later unrelated human edits");
    }
    public static async Task RunLive(MainWindow main, string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "live-history-vault"); Directory.CreateDirectory(root);
        var summary = Source(root, directory, "We agreed that the Cedar launch review will happen on Friday. Sam will prepare the checklist before that review.");
        using var provider = new OpenAiMaintenance(new ApiKeyStore(directory, "OpenAI").Load, () => new());
        var engine = new VaultMaintenance(root); var clock = System.Diagnostics.Stopwatch.StartNew();
        var receipt = await Task.Run(async () => { using var held = engine.Acquire(); return await engine.Process(summary, provider, default); });
        var added = string.Join("\n", receipt.Sections.Select(s => s.Added));
        File.WriteAllText(Path.Combine(directory, "history-live-results.json"), JsonSerializer.Serialize(new { elapsedMs = clock.ElapsedMilliseconds, receipt, diff = engine.Git.Diff(engine.Git.Head) }, new JsonSerializerOptions { WriteIndented = true }));
        check(receipt.Sections.Length > 0 && added.Contains("Friday", StringComparison.OrdinalIgnoreCase) && added.Contains("#^t000000|source"), "Real structured generation applies dated, cited synthetic meeting facts");
        check(File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")).Contains("Existing human project description"), "Real update preserves original human note content");
        using (engine.Acquire()) engine.Revert(receipt.MeetingId);
        check(engine.Receipt(receipt.MeetingId)?.State == "Reverted", "Real generated update supports selective local undo");
    }
}
