using SecondBrain.Core;

internal static class SessionContextTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Client briefs persist independently and preserve invalid-save originals", () =>
        {
            var directory = folder("client-contexts"); var store = new SessionContextStore(directory); var book = store.Load();
            check(book.Profiles.Single().ProfileId == Guid.Empty && !File.Exists(store.FilePath), "Missing context should not mutate storage");
            var client = new SessionContext { ProfileId = Guid.NewGuid(), Client = "Cedar", Project = "Discovery", Goal = "Clarify delivery dates", Participants = ["Morgan", "Riley"], Vocabulary = ["milestone", "handover"] };
            book = store.SaveProfile(book, client); var loaded = new SessionContextStore(directory).Load();
            check(loaded.SelectedProfileId == client.ProfileId && loaded.Profiles.Single(p => p.ProfileId == client.ProfileId).Participants.SequenceEqual(client.Participants), "Profile did not round-trip");
            var before = File.ReadAllBytes(store.FilePath);
            try { store.SaveProfile(book, client with { ProfileId = Guid.NewGuid(), Client = "CEDAR" }); throw new Exception("Duplicate accepted"); } catch (InvalidDataException) { }
            check(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Invalid save changed original profiles");
            check(!(client with { Vocabulary = Enumerable.Repeat("term", 101).ToArray() }).IsValid && !(client with { Goal = new string('g', 2001) }).IsValid, "Unbounded brief accepted");
        });
        test("Recorded context snapshots retain session identity and leave legacy recordings readable", () =>
        {
            var recording = folder("context-recording"); var id = Guid.NewGuid();
            check(SessionContextStore.ReadSnapshot(recording) is null, "Legacy recordings require no migration");
            var client = new SessionContext { ProfileId = Guid.NewGuid(), Client = "Cedar", Goal = "Discuss scope", Participants = ["Morgan"] };
            SessionContextStore.SaveSnapshot(recording, id, client); client.Participants[0] = "Later edit";
            var saved = SessionContextStore.ReadSnapshot(recording)!;
            check(saved.SessionId == id && saved.Context.Participants.Single() == "Morgan", "Snapshot changed with later profile edits");
            check(saved.Context.CombineWith("Existing notes").Contains("Existing notes") && saved.Context.PromptBrief().Contains("not confirmed speaker identities"), "Brief erased existing notes or asserted speaker identity");
            try { saved.Context.CombineWith(new string('n', 16000)); throw new Exception("Oversized context accepted"); } catch (InvalidDataException) { }
        });
    }
}
