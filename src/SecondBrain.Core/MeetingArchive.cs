using System.Security.Cryptography;
using System.Text.Json;

namespace SecondBrain.Core;

public sealed record ArchivedSource(Guid ClientId, string File, int Line, string Text);
public sealed record ArchivedAnswer(Guid RequestId, double Seconds, string Question, string Answer, string State, ArchivedSource[] Sources);
public sealed record MeetingArchive(int Schema, Guid SessionId, Guid ClientId, string TranscriptHash, ArchivedAnswer[] Answers, bool Limited)
{
    public const string FileName = "meeting-evidence.json";
    public static string TranscriptHashAt(string directory)
    {
        using var file = File.OpenRead(LocalBackup.Root(Path.Combine(directory, "transcript.jsonl")));
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public static MeetingArchive? Read(string directory)
    {
        var path = LocalBackup.Root(Path.Combine(directory, FileName));
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 20_000_000) throw new InvalidDataException("Saved evidence exceeds the review limit.");
        var result = JsonSerializer.Deserialize<MeetingArchive>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty evidence archive.");
        Validate(directory, result); return result;
    }
    public static void Save(string directory, ArchivedAnswer[] answers, bool limited)
    {
        directory = LocalBackup.Root(directory);
        using var held = new FileStream(LocalBackup.Root(Path.Combine(directory, "recording.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = RecordingSession.ReadManifest(directory);
        var context = SessionContextStore.ReadSnapshot(directory);
        if (context is not null && context.SessionId != manifest.Id) throw new InvalidDataException("Meeting context belongs to a different recording.");
        var result = new MeetingArchive(1, manifest.Id, context?.Context.ProfileId ?? Guid.Empty, TranscriptHashAt(directory), answers, limited);
        Validate(directory, result);
        var path = LocalBackup.Root(Path.Combine(directory, FileName)); var temporary = LocalBackup.Root(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, result); file.Flush(true); }
            File.Move(temporary, path); // Frozen evidence is never silently replaced.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(string directory, MeetingArchive archive)
    {
        var manifest = RecordingSession.ReadManifest(directory); var context = SessionContextStore.ReadSnapshot(directory);
        if (archive.Schema != 1 || archive.SessionId != manifest.Id || archive.SessionId == Guid.Empty
            || context is not null && context.SessionId != manifest.Id
            || archive.ClientId != (context?.Context.ProfileId ?? Guid.Empty) || archive.TranscriptHash != TranscriptHashAt(directory)
            || archive.Answers is not { Length: <= 256 } || archive.Answers.Any(a => a is null))
            throw new InvalidDataException("Saved evidence does not match this meeting and its original transcript.");
        if (archive.Answers.Select(a => a.RequestId).Distinct().Count() != archive.Answers.Length) throw new InvalidDataException("Duplicate answer identity.");
        foreach (var answer in archive.Answers)
        {
            if (answer.RequestId == Guid.Empty || !double.IsFinite(answer.Seconds) || answer.Seconds < 0 || answer.Seconds > 86400
                || answer.Question is not { Length: <= 2000 } || answer.Answer is not { Length: <= 20000 } || answer.State is not { Length: <= 200 }
                || answer.Sources is not { Length: <= 12 }) throw new InvalidDataException("Invalid saved answer.");
            foreach (var source in answer.Sources)
                if (source is null || source.ClientId != archive.ClientId || source.File is not { Length: > 0 and <= 500 }
                    || Path.IsPathRooted(source.File) || source.File.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "")
                    || source.Line < 1 || source.Text is not { Length: <= 2000 }) throw new InvalidDataException("Invalid or out-of-scope saved source.");
        }
    }
}
