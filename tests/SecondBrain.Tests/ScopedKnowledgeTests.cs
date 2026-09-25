using SecondBrain.Core;

internal static class ScopedKnowledgeTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Client scope excludes foreign, unassigned and malformed notes before keyword and vector ranking", () =>
        {
            var root = folder("scoped-knowledge"); var a = Guid.NewGuid(); var b = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, "a.md"), $"---\nclient_id: {a}\nproject: Shared\n---\n# Launch\nCEDAR launch Friday.");
            File.WriteAllText(Path.Combine(root, "b.md"), $"---\nclient_id: {b}\nproject: Shared\n---\n# Launch\nACORN_SECRET launch Monday.");
            File.WriteAllText(Path.Combine(root, "old.md"), "# Launch\nUNASSIGNED launch Tuesday.");
            File.WriteAllText(Path.Combine(root, "bad.md"), "---\nclient_id: broken\n---\n# Launch\nBROKEN launch.");
            var index = new VaultIndex(); index.Rebuild(root); var vectors = index.Chunks.ToDictionary(c => c.Id, _ => new float[] { 1, 0 });
            foreach (var query in new[] { "launch", "release timing", "ACORN_SECRET", "UNASSIGNED" })
            {
                var result = index.Search(query, new(ClientId: a), vectors, [1, 0]);
                check(result.Hits.All(h => h.Chunk.ClientId == a) && !result.Evidence.Contains("ACORN_SECRET") && !result.Evidence.Contains("UNASSIGNED"), "Cross-client leakage");
            }
            check(index.Search("launch", new(ClientId: Guid.Empty), vectors, [1, 0]).Hits.All(h => h.Chunk.File == "old.md"), "General context included client/private or malformed scope");
            check(ScopedKnowledge.Answer(index.Search("unknownterm", new(ClientId: a), new Dictionary<string, float[]>())).Contains("don’t have evidence"), "Missing evidence did not abstain");
        });
        test("Source assignment is reversible and scoped imports preserve duplicates per client", () =>
        {
            var root = folder("scope-assignment"); VaultFiles.Initialize(root); var source = Path.Combine(folder("scope-source"), "brief.txt"); File.WriteAllText(source, "Cobalt launch Friday.");
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            var first = DocumentImports.Import(root, source, "Shared", clientId: a); var second = DocumentImports.Import(root, source, "Shared", clientId: b);
            check(first.Document!.Id != second.Document!.Id && first.Document.ClientId == a, "Client identity was lost from import deduplication");
            var path = first.Document.Note; var text = File.ReadAllText(VaultFiles.SafePath(root, path));
            ScopedKnowledge.Assign(root, path, b, VaultIndex.Hash(text)); var changed = File.ReadAllText(VaultFiles.SafePath(root, path));
            check(VaultIndex.Parse(path, changed).All(c => c.ClientId == b), "Explicit source assignment failed");
            try { ScopedKnowledge.Assign(root, path, a, VaultIndex.Hash(text)); throw new Exception("Stale assignment accepted"); } catch (VaultConflictException) { }
            ScopedKnowledge.Assign(root, path, a, VaultIndex.Hash(changed)); check(VaultIndex.Parse(path, File.ReadAllText(VaultFiles.SafePath(root, path))).All(c => c.ClientId == a), "Assignment could not be reversed");
        });
        test("Aliases retrieve dated conflicting client decisions without inventing a current fact", () =>
        {
            var root = folder("scoped-conflicts"); var client = Guid.NewGuid();
            foreach (var day in new[] { "Friday", "Monday" }) File.WriteAllText(Path.Combine(root, day + ".md"), $"---\nclient_id: {client}\nkind: Decision\ntitle: Release date\naliases: [\"launch gate\"]\nstatus: Confirmed\ndate: 2026-01-02\n---\nRelease is {day}.");
            var index = new VaultIndex(); index.Rebuild(root); var result = index.Search("launch gate", new(ClientId: client), new Dictionary<string, float[]>());
            check(result.Hits.Count == 2 && result.Conflicts.Length == 1, "Alias or conflict detection failed");
            var answer = ScopedKnowledge.Answer(result); check(answer.Contains("Friday") && answer.Contains("Monday") && answer.Contains("[S1]") && answer.Contains("2026-01-02"), "Conflict answer omitted dated citations");
        });
    }
}
