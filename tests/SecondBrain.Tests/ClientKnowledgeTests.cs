using SecondBrain.Core;

internal static class ClientKnowledgeTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Client records retain Markdown, aliases and reversible sourced confirmation", () =>
        {
            var root = folder("client-records"); VaultFiles.Initialize(root); var client = Guid.NewGuid(); var store = new ClientKnowledgeStore(root);
            File.WriteAllText(Path.Combine(root, "Source.md"), "Taylor will send the report Friday.\n");
            var item = store.Save(new() { ClientId = client, Kind = KnowledgeKind.Commitment, Name = "Report", Aliases = ["weekly delivery", "发布"], Text = "# Report\n\nFollow up on the report.",
                Source = "Source.md", Quote = "Taylor will send the report Friday.", Date = new(2026, 1, 2), Owner = "Taylor", Due = "Friday" });
            item = store.SetConfirmed(item, true); check(item.Value.Status == "Confirmed", "Explicit confirmation failed");
            item = store.SetConfirmed(item, false); check(item.Value.Status == "Observation", "Confirmation could not be reversed");
            var loaded = store.List(client).Documents.Single(); check(loaded.Value.Aliases[0] == "weekly delivery" && loaded.Value.Owner == "Taylor", "Metadata did not round trip");
            var index = new VaultIndex(); index.Rebuild(root); check(index.Search("发布", new(ClientId: client), new Dictionary<string, float[]>()).Hits.Count == 1, "Escaped Unicode alias was not searchable");
            check(store.List(Guid.NewGuid()).Documents.Length == 0, "Catalog crossed client boundaries");
            File.AppendAllText(VaultFiles.SafePath(root, item.Value.Relative), "\nManual detail.");
            try { store.SetConfirmed(item, true); throw new Exception("Stale save accepted"); } catch (VaultConflictException) { }
            var manual = store.Read(item.Value.Relative); var next = store.SetConfirmed(manual, true); check(next.Value.Text.Contains("Manual detail"), "Manual body lost");
        });
        test("Client facts reject unsupported owners, invented source quotes and undated confirmation", () =>
        {
            var root = folder("client-validation"); VaultFiles.Initialize(root); var store = new ClientKnowledgeStore(root); File.WriteAllText(Path.Combine(root, "Source.md"), "Confirmed sentence.");
            var sample = new ClientKnowledge { ClientId = Guid.NewGuid(), Name = "Person", Source = "Source.md", Quote = "Confirmed sentence.", Date = new(2026, 1, 2) };
            foreach (var invalid in new[] { sample with { Owner = "Invented" }, sample with { Quote = "Invented quote" }, sample with { Status = "Confirmed", Date = null }, sample with { Source = "../outside.md" } })
            { try { store.Save(invalid); throw new Exception("Invalid knowledge accepted"); } catch (InvalidDataException) { } }
        });
    }
}
