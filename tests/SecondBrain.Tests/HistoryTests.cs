using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core;

internal static class HistoryTests
{
    private sealed class Provider(Func<MaintenanceInput, MaintenanceProposal> generate) : IMaintenanceProvider
    {
        public int Calls { get; private set; }
        public Task<MaintenanceProposal> Propose(MaintenanceInput input, CancellationToken cancellation) { Calls++; return Task.FromResult(generate(input)); }
    }
    private static string Source(string root, Guid id, string text)
    {
        var folder = $"Meetings/2026-09-24-{id:N}-fixture";
        VaultFiles.Create(root, folder + "/Summary.md", "# Fixture summary");
        VaultFiles.Create(root, folder + "/Transcript.md", "# Original transcript\n" + text + "\n\n^t000000\n");
        VaultFiles.Create(root, folder + "/transcript.jsonl", JsonSerializer.Serialize(new TranscriptEntry(id, "Final", AudioSource.System, 1, 2, text), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }) + "\n");
        return folder + "/Summary.md";
    }
    private static MaintenanceProposal Proposal(MaintenanceInput input) => new([new("Projects/Cedar.md", [new(input.Evidence[0].Text, [new(input.Evidence[0].Id, input.Evidence[0].Text)])])]);
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Meeting note maintenance cannot read or publish into another client's notes", () =>
        {
            var root = folder("history-client-scope"); var client = Guid.NewGuid(); var other = Guid.NewGuid();
            VaultFiles.Create(root, "Projects/Foreign.md", $"---\nclient_id: {other}\n---\n# FOREIGN_SECRET\nFriday review.");
            var engine = new VaultMaintenance(root); using var held = engine.Acquire();
            var summary = Source(root, Guid.NewGuid(), "Cedar review Friday."); var transcript = summary.Replace("Summary.md", "Transcript.md");
            var path = VaultFiles.SafePath(root, transcript); File.WriteAllText(path, $"---\nclient_id: {client}\n---\n" + File.ReadAllText(path));
            var provider = new Provider(input =>
            {
                check(input.Notes.Length > 0 && input.Notes.All(n => !n.Text.Contains("FOREIGN_SECRET") && VaultIndex.Parse(n.Path, n.Text).All(c => c.ClientId == client)), "Provider saw foreign notes");
                return new([new("Projects/Foreign.md", [new(input.Evidence[0].Text, [new(input.Evidence[0].Id, input.Evidence[0].Text)])])]);
            });
            try { engine.Process(summary, provider, default).GetAwaiter().GetResult(); throw new Exception("Foreign publication accepted"); } catch (InvalidDataException) { }
            check(!File.ReadAllText(VaultFiles.SafePath(root, "Projects/Foreign.md")).Contains("Cedar review"), "Foreign note was changed");
        });
        test("Private Git saves manual edits, sourced AI updates, idempotency and selective undo", () =>
        {
            var root = folder("history-main"); VaultFiles.Create(root, "Projects/Cedar.md", "# Cedar\nManual introduction.\n");
            var engine = new VaultMaintenance(root); using var held = engine.Acquire(); engine.Initialize();
            var id = Guid.NewGuid(); var summary = Source(root, id, "The Cedar trial is scheduled for Friday.");
            var provider = new Provider(Proposal); var target = Path.Combine(root, "Projects/Cedar.md");
            File.AppendAllText(target, "Manual edit before AI.\n");
            var receipt = engine.Process(summary, provider, default).GetAwaiter().GetResult();
            var applied = File.ReadAllText(target); var head = engine.Git.Head;
            check(applied.Contains("Manual edit before AI") && applied.Contains("#^t000000|source") && receipt.Sections.Length == 1, "Manual content/provenance missing");
            check(Directory.Exists(Path.Combine(root, ".secondbrain", "history.git", "objects")) && !Directory.Exists(Path.Combine(root, ".git")), "History not separate");
            var manualCommit = engine.Git.Log().Split('\n')[1].Split(' ')[0];
            check(engine.Git.Diff(head).Contains("The Cedar trial") && engine.Git.Diff(manualCommit).Contains("Manual edit before AI"), "Readable history or manual checkpoint missing");
            check(engine.Git.Diff(head).Contains("Meeting update · 2026") && !engine.Git.Log().Contains("Â"), "Unicode history decoded incorrectly");
            engine.Process(summary, provider, default).GetAwaiter().GetResult(); check(provider.Calls == 1 && File.ReadAllText(target) == applied, "Reprocessing duplicated an update");
            var second = Guid.NewGuid(); engine.Process(Source(root, second, "The Pine review is scheduled for Monday."), provider, default).GetAwaiter().GetResult();
            File.AppendAllText(target, "\nUnrelated later manual edit.\n");
            engine.Revert(id); var reverted = File.ReadAllText(target);
            check(!reverted.Contains("Cedar trial") && reverted.Contains("Pine review") && reverted.Contains("Unrelated later manual edit") && reverted.Contains("Manual edit before AI"), "Selective revert lost unrelated later work");
            engine.Process(summary, provider, default).GetAwaiter().GetResult(); check(engine.Receipt(id)!.State == "Reverted" && !File.ReadAllText(target).Contains("Cedar trial"), "Retry reapplied an intentionally reverted update");
            File.WriteAllText(target, File.ReadAllText(target).Replace("Pine review", "My edited review")); var edited = File.ReadAllText(target);
            try { engine.Revert(second); throw new Exception("Expected conflict"); } catch (VaultConflictException) { }
            check(File.ReadAllText(target) == edited, "Conflict overwrote user-edited generated section");
        });
        test("Concurrent Markdown edits force regeneration; invalid citations and destinations never publish", () =>
        {
            var root = folder("history-race"); VaultFiles.Create(root, "Projects/Cedar.md", "# Cedar\nOriginal manual note.\n");
            var engine = new VaultMaintenance(root); using var held = engine.Acquire();
            var summary = Source(root, Guid.NewGuid(), "The owner is Sam for the Friday review."); var changed = false;
            var provider = new Provider(input => { if (!changed) { File.AppendAllText(Path.Combine(root, "Projects/Cedar.md"), "Concurrent Obsidian edit.\n"); changed = true; } else check(input.Notes.Any(n => n.Text.Contains("Concurrent Obsidian")), "Regeneration did not see fresh notes"); return Proposal(input); });
            engine.Process(summary, provider, default).GetAwaiter().GetResult();
            check(provider.Calls == 2 && File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")).Contains("Concurrent Obsidian"), "Edit was overwritten or not recomputed");
            var invalid = Source(root, Guid.NewGuid(), "No launch date was agreed."); var original = File.ReadAllText(Path.Combine(root, "Projects/Cedar.md"));
            foreach (var bad in new Provider[] { new(i => new([new("../escape.md", [new("Bad", [new("t000000", i.Evidence[0].Text)])])])), new(i => new([new("Projects/Cedar.md", [new("Invented commitment.", [new("t000000", "A quote that does not exist")])])])) })
            { try { engine.Process(invalid, bad, default).GetAwaiter().GetResult(); throw new Exception("Bad proposal accepted"); } catch (InvalidDataException) { } }
            check(File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")) == original && !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.md")), "Invalid proposal changed notes");
        });
        test("Interrupted multi-file publication recovers once; failure rolls back; recovery conflicts retain backups", () =>
        {
            var root = folder("history-recovery"); VaultFiles.Create(root, "Projects/Cedar.md", "# Cedar\nPrior manual text.\n");
            var engine = new VaultMaintenance(root); using var held = engine.Acquire(); var id = Guid.NewGuid(); var source = Source(root, id, "The team agreed to a Friday review.");
            var provider = new Provider(Proposal);
            try { engine.Process(source, provider, default, _ => throw new SimulatedVaultCrashException()).GetAwaiter().GetResult(); throw new Exception("Crash not injected"); } catch (SimulatedVaultCrashException) { }
            check(engine.Transaction.Pending && engine.Git.Log().Contains("Manual"), "Interrupted work lost its journal/prior history");
            var restarted = new VaultMaintenance(root); restarted.Initialize(); var text = File.ReadAllText(Path.Combine(root, "Projects/Cedar.md"));
            check(!restarted.Transaction.Pending && restarted.Receipt(id)?.State == "Applied" && text.Contains("Friday review"), "Recovery did not complete publication");
            restarted.Process(source, provider, default).GetAwaiter().GetResult(); check(provider.Calls == 1 && File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")) == text, "Recovered update duplicated");
            var fail = Source(root, Guid.NewGuid(), "The budget review is on Tuesday.");
            try { restarted.Process(fail, provider, default, _ => throw new IOException("Injected disk-write failure")).GetAwaiter().GetResult(); throw new Exception("Failure not injected"); } catch (IOException) { }
            check(File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")) == text && !restarted.Transaction.Pending, "Failure did not restore prior bytes");
            try { restarted.Process(fail, provider, default, _ => throw new SimulatedVaultCrashException()).GetAwaiter().GetResult(); } catch (SimulatedVaultCrashException) { }
            File.WriteAllText(Path.Combine(root, "Projects/Cedar.md"), "Manual edit following an interrupted write.");
            try { restarted.Initialize(); throw new Exception("Recovery conflict not detected"); } catch (VaultConflictException) { }
            check(restarted.Transaction.Pending && File.ReadAllText(Path.Combine(root, "Projects/Cedar.md")).StartsWith("Manual edit following"), "Recovery overwrote concurrent user content or discarded backup");
        });
    }
}
