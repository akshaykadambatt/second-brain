using System.IO;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record VaultOptions(string Folder = "Vault", bool Semantic = true, string Project = "", DateOnly? From = null, DateOnly? Until = null);
internal sealed class VaultSettings(string data, string home)
{
    private readonly string path = Path.Combine(data, "vault-settings.json");
    public VaultOptions Load() => File.Exists(path) ? JsonSerializer.Deserialize<VaultOptions>(File.ReadAllText(path)) ?? new() : new();
    public string Resolve(VaultOptions value) => Path.GetFullPath(Path.Combine(home, value.Folder));
    public void Save(VaultOptions value)
    {
        if (value.From > value.Until || string.IsNullOrWhiteSpace(value.Folder)) throw new InvalidOperationException("Check vault folder and date range.");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(path + ".tmp", path, true);
    }
    public string Portable(string root)
    {
        var relative = Path.GetRelativePath(home, root);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative) ? Path.GetFullPath(root) : relative;
    }
}

internal sealed class KnowledgeService : IKnowledgeSearch, IDisposable
{
    private sealed record Cache(string Model, int Dimensions, Dictionary<string, float[]> Vectors);
    private readonly IEmbeddingProvider? embeddings;
    private readonly string cachePath;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private Dictionary<string, float[]> vectors = [];
    private readonly VaultIndex index = new();
    private Task refresh = Task.CompletedTask;
    private DateTime nextRefresh, retryEmbedding;
    public string Root { get; }
    public KnowledgeFilter Filter { get; }
    public string Status { get; private set; } = "Index waiting";
    public IReadOnlyList<NoteChunk> Chunks => index.Chunks;
    public KnowledgeService(string root, string data, KnowledgeFilter filter, IEmbeddingProvider? embeddings)
    {
        Root = Path.GetFullPath(root); Filter = filter; this.embeddings = embeddings;
        cachePath = Path.Combine(data, "knowledge-" + VaultIndex.Hash(Root)[..16] + ".json");
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length < 80_000_000)
            {
                var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(cachePath));
                if (cache is { Model: OpenAiEmbeddings.Model, Dimensions: OpenAiEmbeddings.Dimensions })
                    vectors = cache.Vectors.Where(v => v.Value is { Length: OpenAiEmbeddings.Dimensions } && v.Value.All(float.IsFinite)).ToDictionary();
            }
        }
        catch (Exception) { Status = "Index cache unavailable; rebuilding from Markdown."; }
    }
    public Task Refresh(bool force = false)
    {
        if (!refresh.IsCompleted || stop.IsCancellationRequested || !force && DateTime.UtcNow < nextRefresh) return refresh;
        nextRefresh = DateTime.UtcNow.AddSeconds(5);
        refresh = Work(); return refresh;
    }
    private async Task Work()
    {
        var rebuilt = false;
        try
        {
            await Task.Run(() => index.Rebuild(Root, stop.Token), stop.Token);
            rebuilt = true;
            var snapshot = index.Chunks; var ids = snapshot.Select(c => c.Id).ToHashSet();
            lock (gate) vectors = vectors.Where(v => ids.Contains(v.Key)).ToDictionary();
            Status = index.Status + " · keyword ready";
            if (embeddings is null || DateTime.UtcNow < retryEmbedding) return;
            NoteChunk[] missing;
            lock (gate) missing = snapshot.Where(c => !vectors.ContainsKey(c.Id)).Take(128).ToArray();
            foreach (var batch in missing.Chunk(32))
            {
                var encoded = await embeddings.Embed(batch.Select(c => c.Title + "\n" + c.Text).ToArray(), stop.Token);
                lock (gate) for (var i = 0; i < batch.Length; i++) vectors[batch[i].Id] = encoded[i];
            }
            int count; Dictionary<string, float[]> saved;
            lock (gate) { count = vectors.Count; saved = new(vectors); }
            if (missing.Length > 0)
            {
                var json = JsonSerializer.Serialize(new Cache(OpenAiEmbeddings.Model, OpenAiEmbeddings.Dimensions, saved));
                await File.WriteAllTextAsync(cachePath + ".tmp", json, stop.Token); File.Move(cachePath + ".tmp", cachePath, true);
            }
            Status = index.Status + $" · semantic {count}/{snapshot.Count} ready";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        { if (!rebuilt) index.Clear(); retryEmbedding = DateTime.UtcNow.AddMinutes(1); Status = index.Status + " · refresh/semantic unavailable (" + ex.GetType().Name + "); keyword results may be incomplete. Retry with Rebuild."; }
    }
    public async Task Rebuild()
    {
        await refresh; lock (gate) vectors.Clear();
        if (File.Exists(cachePath)) File.Delete(cachePath);
        retryEmbedding = DateTime.MinValue; await Refresh(true);
    }
    public async Task<KnowledgeResult> Search(string question, CancellationToken cancellation)
    {
        Dictionary<string, float[]> saved; lock (gate) saved = new(vectors);
        float[]? query = null; var status = "Keyword search";
        if (embeddings is not null && saved.Count > 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token); deadline.CancelAfter(1500);
            try { query = (await embeddings.Embed([question], deadline.Token))[0]; status = "Keyword + semantic search"; }
            catch (Exception) when (!cancellation.IsCancellationRequested) { status = "Semantic unavailable/slow · keyword fallback"; }
        }
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => index.Search(question, Filter, saved, query, status + " · " + Status), cancellation);
    }
    public async Task Stop() { stop.Cancel(); await refresh; }
    public void Dispose() { stop.Dispose(); (embeddings as IDisposable)?.Dispose(); }
}
