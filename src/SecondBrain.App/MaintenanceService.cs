using System.IO;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record MaintenanceJob(string Id, string Summary, string State, string Message, bool Requested = false);
internal sealed record HistoryView(string Log, MaintenanceReceipt[] Updates, MaintenanceJob[] Jobs);
internal sealed class EmptyMaintenance : IMaintenanceProvider
{ public Task<MaintenanceProposal> Propose(MaintenanceInput input, CancellationToken cancellation) => Task.FromResult(new MaintenanceProposal([])); }

internal sealed class MaintenanceService : IDisposable
{
    private readonly VaultMaintenance engine;
    private readonly IMaintenanceProvider provider;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly object jobsGate = new();
    private readonly Dictionary<string, MaintenanceJob> jobs = [];
    private Task worker;
    private readonly bool enabled;
    public string Root => engine.Root;
    public string Status { get; private set; } = "Opening local vault history…";
    public bool Busy { get; private set; }
    public long Revision { get; private set; }
    public MaintenanceService(string root, IMaintenanceProvider provider, bool enabled)
    {
        engine = new(root); this.provider = provider; this.enabled = enabled;
        var folder = VaultFiles.SafePath(root, ".secondbrain/jobs"); Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var job = JsonSerializer.Deserialize<MaintenanceJob>(File.ReadAllText(VaultFiles.SafePath(root, Path.GetRelativePath(root, file)))) ?? throw new InvalidDataException("Unreadable pending job.");
            if (job.State == "Working") job = job with { State = "Pending", Message = "Interrupted analysis will retry." };
            jobs[job.Id] = job;
        }
        worker = Task.Run(Work);
    }
    private void Save(MaintenanceJob job)
    {
        lock (jobsGate)
        {
            VaultTransaction.SaveJson(VaultFiles.SafePath(Root, ".secondbrain/jobs/" + job.Id + ".json"), job);
            jobs[job.Id] = job; Revision++;
        }
    }
    public void Queue(string summary, bool requested = false)
    {
        VaultFiles.SafePath(Root, summary);
        var id = VaultIndex.Hash(summary)[..24];
        lock (jobsGate) { if (jobs.TryGetValue(id, out var old) && old.State is "Done" or "Working" or "Pending") return; }
        Save(new(id, summary, "Pending", enabled || requested ? "Waiting to update notes." : "Automatic updates are off; use Retry pending to process.", requested));
    }
    public void Retry()
    {
        MaintenanceJob[] pending; lock (jobsGate) pending = jobs.Values.Where(j => j.State is "Failed" or "Pending").ToArray();
        foreach (var job in pending) Save(job with { State = "Pending", Message = "Retry requested.", Requested = true });
        if (worker.IsCompleted && !stop.IsCancellationRequested) worker = Task.Run(Work);
    }
    public void QueueSavedMeetings(bool requested = false)
    {
        var folder = VaultFiles.SafePath(Root, "Meetings"); if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "Summary.md", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Order())
            Queue(Path.GetRelativePath(Root, path).Replace('\\', '/'), requested);
    }
    private async Task Work()
    {
        try
        {
            await Exclusive(() => { engine.Initialize(); Status = enabled ? "Local history ready · automatic updates on" : "Local history ready · automatic updates off"; return true; }, stop.Token);
            if (enabled) QueueSavedMeetings();
            while (!stop.IsCancellationRequested)
            {
                MaintenanceJob? job; lock (jobsGate) job = jobs.Values.FirstOrDefault(j => j.State == "Pending" && (enabled || j.Requested));
                if (job is null) { await Task.Delay(300, stop.Token); continue; }
                Busy = true; Save(job with { State = "Working", Message = "Analyzing meeting and protecting manual edits…" }); Status = "Updating Markdown from meeting…";
                try
                {
                    await gate.WaitAsync(stop.Token);
                    try
                    {
                        using var held = engine.Acquire();
                        var receipt = await engine.Process(job.Summary, provider, stop.Token);
                        Save(job with { State = "Done", Message = receipt.State == "Reverted" ? "Already reverted; left unchanged." : receipt.Sections.Length + " notes updated; sources and local history saved." });
                        Status = "Meeting update saved in local history.";
                    }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { Save(job with { State = "Pending", Message = "Paused on shutdown; will resume next launch." }); }
                catch (Exception ex) { Status = "Update needs attention: " + Explain(ex); Save(job with { State = "Failed", Message = Status }); }
                finally { Busy = false; Revision++; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Status = "History unavailable: " + Explain(ex); Revision++; }
    }
    private static string Explain(Exception ex) => ex is IOException or InvalidOperationException ? ex.Message : "Check vault access and Git for Windows, then reconnect the vault.";
    private async Task<T> Exclusive<T>(Func<T> action, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try { return await Task.Run(() => { using var held = engine.Acquire(); return action(); }, cancellation); }
        finally { gate.Release(); }
    }
    public Task<HistoryView> ReadHistory() => Exclusive(() =>
    { MaintenanceJob[] snapshot; lock (jobsGate) snapshot = jobs.Values.ToArray(); return new HistoryView(engine.Git.Log(), engine.Receipts(), snapshot); }, stop.Token);
    public Task<string> Diff(string commit) => Exclusive(() => engine.Git.Diff(commit), stop.Token);
    public async Task<ImportResult[]> ImportDocuments(string[] paths, string project, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation);
        await gate.WaitAsync(linked.Token);
        try { return await Task.Run(() => paths.Select(path => DocumentImports.Import(Root, path, project, linked.Token)).ToArray(), linked.Token); }
        finally { gate.Release(); Revision++; }
    }
    public async Task Revert(Guid meeting)
    { await Exclusive(() => { engine.Revert(meeting); return true; }, stop.Token); Status = "Selected update reverted; later unrelated edits retained."; Revision++; }
    public async Task Recover()
    { await Exclusive(() => { engine.Initialize(); return true; }, stop.Token); Status = "Recovery complete. Retry pending updates when ready."; Revision++; }
    public async Task Stop() { stop.Cancel(); await worker; await gate.WaitAsync(); gate.Release(); }
    public void Dispose() { stop.Dispose(); (provider as IDisposable)?.Dispose(); }
}
