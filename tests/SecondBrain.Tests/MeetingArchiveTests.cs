using System.Text.Json;
using SecondBrain.Core;

internal static class MeetingArchiveTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        string Fixture(string name, Guid client)
        {
            var dir = folder(name); var manifest = new RecordingManifest { State = "Completed", Tracks = [new(AudioSource.Microphone, "fixture", "Mic", 16000), new(AudioSource.System, "fixture", "System", 16000)] };
            File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(manifest));
            SessionContextStore.SaveSnapshot(dir, manifest.Id, new() { ProfileId = client, Client = client == Guid.Empty ? "" : "Fixture client" });
            using var log = new TranscriptLog(dir, manifest.Id); log.Append(new(manifest.Id, "Final", AudioSource.System, 1, 2, "Original.")); return dir;
        }
        ArchivedAnswer Answer(Guid client) => new(Guid.NewGuid(), 1, "Question?", "Saved answer.", "Complete", [new(client, "Clients/note.md", 4, "Exact source passage.")]);
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; } throw new Exception("Invalid archive accepted"); }
        test("Saved answer evidence is immutable, source linked and bound to the original meeting", () =>
        {
            var client = Guid.NewGuid(); var dir = Fixture("archive-original", client); var original = File.ReadAllBytes(Path.Combine(dir, "transcript.jsonl"));
            MeetingArchive.Save(dir, [Answer(client)], false); var read = MeetingArchive.Read(dir)!;
            check(read.Answers[0].Sources[0].Line == 4 && read.ClientId == client, "Source scope/location lost");
            Reject(() => MeetingArchive.Save(dir, [], false));
            check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "transcript.jsonl"))), "Original rewritten");
            File.AppendAllText(Path.Combine(dir, "transcript.jsonl"), " "); Reject(() => MeetingArchive.Read(dir));
        });
        test("Meeting archives refuse cross-client sources, escaped source paths and active writers", () =>
        {
            var client = Guid.NewGuid(); var dir = Fixture("archive-invalid", client);
            Reject(() => MeetingArchive.Save(dir, [Answer(Guid.NewGuid())], false));
            Reject(() => MeetingArchive.Save(dir, [Answer(client) with { Sources = [new(client, "../private.md", 1, "Unsafe")] }], false));
            using var held = new FileStream(Path.Combine(dir, "recording.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Reject(() => MeetingArchive.Save(dir, [Answer(client)], false));
            check(!File.Exists(Path.Combine(dir, MeetingArchive.FileName)), "Invalid evidence was saved");
        });
        test("Legacy recordings have no invented archive and oversized answers fail safely", () =>
        {
            var dir = Fixture("archive-legacy", Guid.Empty); check(MeetingArchive.Read(dir) is null, "Invented evidence");
            Reject(() => MeetingArchive.Save(dir, [Answer(Guid.Empty) with { Answer = new string('a', 20001) }], false));
            MeetingArchive.Save(dir, [Answer(Guid.Empty)], true); check(MeetingArchive.Read(dir)!.Limited, "Truncation warning lost");
        });
    }
}
