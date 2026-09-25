using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public static class ScopedKnowledge
{
    // An extractive answer cannot silently introduce unsourced client facts.
    public static string Answer(KnowledgeResult result) => result.Hits.Count == 0
        ? "I don’t have evidence for that in this client’s knowledge. Add or assign relevant source notes, or try a more specific question."
        : (result.Conflicts.Length > 0 ? "Conflicting or changed accounts need review:\n" + string.Join('\n', result.Conflicts) + "\n\n" : "Relevant source passages (not a verified current-fact summary):\n\n")
            + string.Join("\n\n", result.Hits.Select((h, i) => $"[S{i + 1}] {h.Chunk.Text}\nSource: {h.Chunk.File}:{h.Chunk.Line} · {h.Chunk.Date?.ToString("yyyy-MM-dd") ?? "undated"} · {h.Chunk.FactStatus}"));
    public static void Assign(string root, string relative, Guid client, string expectedHash)
    {
        if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Clients/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Assign source notes; curated records keep their original client identity.");
        var engine = new VaultMaintenance(root); using var held = engine.Acquire(); engine.Initialize();
        var path = VaultFiles.SafePath(root, relative); if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Source note exceeds 2 MB.");
        var before = File.ReadAllBytes(path); var text = Encoding.UTF8.GetString(before);
        if (VaultIndex.Hash(text) != expectedHash) throw new VaultConflictException("The note changed. Search again before assigning it.");
        var normalized = text.Replace("\r", ""); var end = normalized.StartsWith("---\n") ? normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal) : -1;
        string after;
        if (end >= 0) after = "---\nclient_id: " + client + "\n" + Regex.Replace(normalized[4..end], @"(?m)^client_id:.*(?:\n|$)", "") + normalized[end..];
        else after = "---\nclient_id: " + client + "\n---\n" + text;
        engine.Transaction.Apply(engine.Capture(), [new(relative, before, Encoding.UTF8.GetBytes(after))], "Assign source note to selected client", "client-assignment-" + Guid.NewGuid().ToString("N"));
    }
}
