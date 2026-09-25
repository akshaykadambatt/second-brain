using System.Text;
using System.Text.Json;

namespace SecondBrain.Core;

public enum KnowledgeKind { Client, Person, Project, Decision, Commitment }
public sealed record ClientKnowledge
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ClientId { get; init; }
    public KnowledgeKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Project { get; init; } = "";
    public string[] Aliases { get; init; } = [];
    public string Status { get; init; } = "Observation";
    public string Text { get; init; } = "";
    public string Source { get; init; } = "";
    public int SourceLine { get; init; } = 1;
    public string Quote { get; init; } = "";
    public DateOnly? Date { get; init; }
    public string Owner { get; init; } = "";
    public string Due { get; init; } = "";
    public string Relative => $"Clients/{ClientId:N}/{Kind}s/{Id:N}.md";
    public override string ToString() => $"{Kind} · {Name} · {Status}";
}
public sealed record KnowledgeDocument(ClientKnowledge Value, string Revision);
public sealed record KnowledgeCatalog(KnowledgeDocument[] Documents, string[] Problems);

// Markdown is authoritative. The Git transaction protects manual edits and provides full history.
public sealed class ClientKnowledgeStore(string root)
{
    public KnowledgeCatalog List(Guid client)
    {
        var folder = VaultFiles.SafePath(root, $"Clients/{client:N}");
        if (!Directory.Exists(folder)) return new([], []);
        var found = new List<KnowledgeDocument>(); var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.md", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(1001))
        {
            if (found.Count + problems.Count >= 1000) { problems.Add("Only the first 1,000 records are shown."); break; }
            try
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/'); var item = Read(relative);
                if (item.Value.ClientId != client || item.Value.Relative != relative) throw new InvalidDataException("Record identity differs from its path.");
                found.Add(item);
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException)
            { problems.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        return new(found.OrderBy(i => i.Value.Kind).ThenBy(i => i.Value.Name).ToArray(), problems.ToArray());
    }
    public KnowledgeDocument Read(string relative)
    {
        var path = VaultFiles.SafePath(root, relative);
        if (new FileInfo(path).Length > 100_000) throw new InvalidDataException("Knowledge record exceeds 100 KB.");
        var text = File.ReadAllText(path); var split = text.Replace("\r", "").Split("\n---\n", 2);
        if (split.Length != 2 || !split[0].StartsWith("---\n")) throw new InvalidDataException("Record frontmatter is missing.");
        var fields = new Dictionary<string, string>();
        foreach (var line in split[0][4..].Split('\n')) { var pair = line.Split(':', 2); if (pair.Length == 2) fields.Add(pair[0], pair[1].Trim()); }
        string Get(string key) => fields.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<string>(value) ?? "" : "";
        if (Get("entity_schema") != "1") throw new InvalidDataException("Unsupported knowledge record version.");
        var value = new ClientKnowledge { Id = Guid.Parse(Get("entity_id")), ClientId = Guid.Parse(Get("client_id")), Kind = Enum.Parse<KnowledgeKind>(Get("kind")), Name = Get("title"),
            Project = Get("project"), Aliases = JsonSerializer.Deserialize<string[]>(fields.GetValueOrDefault("aliases", "[]")) ?? [], Status = Get("status"),
            Source = Get("source"), SourceLine = int.Parse(Get("source_line")), Quote = Get("quote"), Date = DateOnly.TryParseExact(Get("date"), "yyyy-MM-dd", out var date) ? date : null,
            Owner = Get("owner"), Due = Get("due"), Text = split[1].Trim() };
        Validate(value); return new(value, VaultIndex.Hash(text));
    }
    public KnowledgeDocument Save(ClientKnowledge value, string? expectedRevision = null)
    {
        Validate(value); var engine = new VaultMaintenance(root);
        using var held = engine.Acquire(); engine.Initialize();
        var path = VaultFiles.SafePath(root, value.Relative); var before = VaultTransaction.Read(path);
        if (before is null ? expectedRevision is not null : expectedRevision != VaultIndex.Hash(Encoding.UTF8.GetString(before)))
            throw new VaultConflictException("This record changed in another editor. Reload before saving; no edits were overwritten.");
        if (value.Source.Length > 0)
        {
            var source = VaultFiles.SafePath(root, value.Source);
            if (!value.Source.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || new FileInfo(source).Length > 2_000_000) throw new InvalidDataException("Choose a readable Markdown source within the vault.");
            var content = File.ReadAllText(source).Replace("\r", "");
            var fromLine = string.Join('\n', content.Split('\n').Skip(value.SourceLine - 1));
            if (value.Quote.Length == 0 || !fromLine.StartsWith(value.Quote.Replace("\r", ""), StringComparison.Ordinal)) throw new InvalidDataException("The quote must match the source at the selected line. Refresh changed sources before confirming.");
        }
        var rendered = Render(value);
        if (before is not null)
        {
            var oldHeader = Encoding.UTF8.GetString(before).Replace("\r", "").Split("\n---\n", 2)[0];
            var known = rendered.Split("\n---\n", 2)[0].Split('\n').Select(l => l.Split(':', 2)[0]).ToHashSet();
            var extra = oldHeader.Split('\n').Where(l => l.Contains(':') && !known.Contains(l.Split(':', 2)[0])).ToArray();
            if (extra.Length > 0) rendered = rendered.Replace("\n---\n", "\n" + string.Join('\n', extra) + "\n---\n");
        }
        var after = Encoding.UTF8.GetBytes(rendered);
        engine.Transaction.Apply(engine.Capture(), [new(value.Relative, before, after)], "Save " + value.Kind + ": " + value.Name, "client-knowledge-" + Guid.NewGuid().ToString("N"));
        return new(value, VaultIndex.Hash(Encoding.UTF8.GetString(after)));
    }
    public KnowledgeDocument SetConfirmed(KnowledgeDocument document, bool confirmed) => Save(document.Value with { Status = confirmed ? "Confirmed" : "Observation" }, document.Revision);
    private static void Validate(ClientKnowledge value)
    {
        if (value.Id == Guid.Empty || value.ClientId == Guid.Empty || !Enum.IsDefined(value.Kind) || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 160
            || value.Name.Any(char.IsControl) || value.Project.Length > 120 || value.Text.Length > 16000 || value.Aliases.Length > 20 || value.Aliases.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 100)
            || value.Status is not ("Observation" or "Confirmed") || value.Source.Length > 500 || value.SourceLine < 1 || value.Quote.Length > 4000 || value.Owner.Length > 100 || value.Due.Length > 100)
            throw new InvalidDataException("Check the record name, client, aliases and content limits.");
        if ((value.Status == "Confirmed" || value.Kind is KnowledgeKind.Decision or KnowledgeKind.Commitment) && (value.Source.Length == 0 || value.Date is null || value.Quote.Length == 0))
            throw new InvalidDataException("Confirmed records, decisions and commitments need a dated source quote.");
        if (value.Owner.Length > 0 && !value.Quote.Contains(value.Owner, StringComparison.OrdinalIgnoreCase) || value.Due.Length > 0 && !value.Quote.Contains(value.Due, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Owner and due wording must occur in the source quote. Leave them blank when unsupported.");
    }
    private static string Render(ClientKnowledge value)
    {
        var text = new StringBuilder("---\n");
        void Field(string key, string content) => text.AppendLine(key + ": " + JsonSerializer.Serialize(content));
        Field("entity_schema", "1"); Field("entity_id", value.Id.ToString()); Field("client_id", value.ClientId.ToString()); Field("kind", value.Kind.ToString()); Field("title", value.Name);
        Field("project", value.Project); text.AppendLine("aliases: " + JsonSerializer.Serialize(value.Aliases)); Field("status", value.Status);
        Field("source", value.Source); Field("source_line", value.SourceLine.ToString()); Field("quote", value.Quote); Field("date", value.Date?.ToString("yyyy-MM-dd") ?? "");
        Field("source_link", value.Source.Length == 0 ? "" : "[[" + value.Source + "]]"); Field("owner", value.Owner); Field("due", value.Due);
        text.AppendLine("---"); text.AppendLine(value.Text.Trim()); return text.ToString();
    }
}
