using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record KnowledgeFilter(string Project = "", DateOnly? From = null, DateOnly? Until = null);
public sealed record NoteChunk(string Id, string File, int Line, string Title, string Text, string Project, DateOnly? Date);
public sealed record KnowledgeHit(NoteChunk Chunk, double Score);
public sealed record KnowledgeResult(IReadOnlyList<KnowledgeHit> Hits, string Status)
{
    public string Evidence => Hits.Count == 0 ? "No relevant vault evidence was found. Do not invent private/project facts."
        : string.Join("\n\n", Hits.Select((h, i) => $"[S{i + 1}] {h.Chunk.File}:{h.Chunk.Line}; date={h.Chunk.Date?.ToString("yyyy-MM-dd") ?? "undated"}; project={h.Chunk.Project}\n{h.Chunk.Text}"));
}
public interface IKnowledgeSearch
{
    Task<KnowledgeResult> Search(string question, CancellationToken cancellation);
}
public interface IEmbeddingProvider
{
    Task<float[][]> Embed(IReadOnlyList<string> text, CancellationToken cancellation);
}

public sealed class VaultIndex
{
    private NoteChunk[] chunks = [];
    public IReadOnlyList<NoteChunk> Chunks => Volatile.Read(ref chunks);
    public string Status { get; private set; } = "Not indexed yet";
    public void Clear() { Volatile.Write(ref chunks, []); Status = "Vault scan unavailable"; }
    private static readonly HashSet<string> StopWords = "a an the of to for in on at is are was were be been and or with from we i you it this that what how when why which who do does did can could would should our your project please tell me about".Split(' ').ToHashSet();
    public static string[] Terms(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{Nd}]{2,}").Select(m => m.Value).Where(t => !StopWords.Contains(t)).ToArray();
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public void Rebuild(string root, CancellationToken cancellation = default)
    {
        var found = new List<NoteChunk>(); var skipped = 0; long bytes = 0; var files = 0;
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var folder))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
            foreach (var sub in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && name is not ("Templates" or "Attachments")) pending.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested(); var info = new FileInfo(file);
                if (info.Name.StartsWith('.') || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (++files > 2000 || (bytes += info.Length) > 40_000_000 || found.Count >= 20000 || info.Length > 2_000_000) { skipped++; continue; }
                try { found.AddRange(Parse(Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        Volatile.Write(ref chunks, found.Take(20000).ToArray());
        Status = $"{files - skipped} Markdown files · {Chunks.Count} passages" + (skipped > 0 || found.Count > 20000 ? " · some files/passages skipped (read error or index limits)" : "");
    }
    public static IReadOnlyList<NoteChunk> Parse(string file, string text)
    {
        var lines = text.Replace("\r", "").Split('\n'); var first = 0; var project = ""; DateOnly? date = null;
        if (lines.Length > 1 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            if (end > 0)
            {
                foreach (var line in lines.Skip(1).Take(end - 1))
                {
                    var pair = line.Split(':', 2); if (pair.Length != 2) continue;
                    var value = pair[1].Trim().Trim('"', '\'');
                    if (pair[0].Trim() == "project") project = value;
                    if (pair[0].Trim() == "date" && DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed)) date = parsed;
                }
                first = end + 1;
            }
        }
        if (date is null && DateOnly.TryParseExact(Path.GetFileName(file)[..Math.Min(10, Path.GetFileName(file).Length)], "yyyy-MM-dd", out var dated)) date = dated;
        var chunks = new List<NoteChunk>(); var title = Path.GetFileNameWithoutExtension(file); var buffer = new StringBuilder(); var start = first + 1;
        void Flush()
        {
            var value = buffer.ToString().Trim(); buffer.Clear();
            if (value.Length > 0) chunks.Add(new(Hash(file + "\n" + start + "\n" + chunks.Count + "\n" + title + "\n" + value), file, start, title, value, project, date));
        }
        for (var i = first; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#')) { Flush(); title = lines[i].TrimStart('#', ' '); }
            if (buffer.Length + lines[i].Length > 1400) Flush();
            if (buffer.Length == 0) start = i + 1;
            // Split very long lines explicitly; no content is silently truncated.
            for (var offset = 0; offset < lines[i].Length; offset += 1200)
            { buffer.AppendLine(lines[i].Substring(offset, Math.Min(1200, lines[i].Length - offset))); if (buffer.Length >= 1200) Flush(); }
            if (lines[i].Length == 0 && buffer.Length >= 350) Flush();
        }
        Flush(); return chunks;
    }
    public KnowledgeResult Search(string query, KnowledgeFilter filter, IReadOnlyDictionary<string, float[]> vectors, float[]? embedding = null, string status = "Keyword search")
    {
        var eligible = Chunks.Where(c => (filter.Project.Length == 0 || c.Project.Equals(filter.Project, StringComparison.OrdinalIgnoreCase))
            && (filter.From is null || c.Date >= filter.From) && (filter.Until is null || c.Date <= filter.Until)).ToArray();
        var terms = Terms(query).Distinct().ToArray();
        var tokens = eligible.ToDictionary(c => c.Id, c => Terms(c.Title + " " + c.Text));
        var idf = terms.ToDictionary(t => t, t => Math.Log(1 + eligible.Length / (1d + tokens.Values.Count(v => v.Contains(t)))));
        var lexical = eligible.Select(c => (Chunk: c, Score: terms.Sum(t =>
        {
            var frequency = tokens[c.Id].Count(w => w == t);
            return frequency == 0 ? 0 : idf[t] * frequency / (frequency + .7 + tokens[c.Id].Length / 150d);
        }))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(40).ToArray();
        var semantic = embedding is null ? [] : eligible.Where(c => vectors.ContainsKey(c.Id)).Select(c => (Chunk: c, Score: Cosine(embedding, vectors[c.Id])))
            .Where(x => x.Score >= .3).OrderByDescending(x => x.Score).Take(40).ToArray();
        var scores = new Dictionary<string, double>();
        foreach (var ranked in new[] { lexical, semantic })
            for (var i = 0; i < ranked.Length; i++) scores[ranked[i].Chunk.Id] = scores.GetValueOrDefault(ranked[i].Chunk.Id) + 1d / (30 + i);
        var hits = eligible.Where(c => scores.ContainsKey(c.Id)).OrderByDescending(c => scores[c.Id]).Select(c => new KnowledgeHit(c, scores[c.Id]))
            .GroupBy(h => h.Chunk.File).SelectMany(g => g.Take(2)).OrderByDescending(h => h.Score).Take(6).ToArray();
        return new(hits, status + (hits.Length == 0 ? " · no relevant evidence" : $" · {hits.Length} source passages"));
    }
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, x = 0, y = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; x += a[i] * a[i]; y += b[i] * b[i]; }
        return x > 0 && y > 0 ? dot / Math.Sqrt(x * y) : 0;
    }
}
