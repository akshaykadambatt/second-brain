using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondBrain.Core;

public sealed record SessionContext
{
    public Guid ProfileId { get; init; }
    public string Client { get; init; } = "";
    public string Project { get; init; } = "";
    public string Goal { get; init; } = "";
    public string[] Participants { get; init; } = [];
    public string[] Vocabulary { get; init; } = [];
    [JsonIgnore] public string DisplayName => Client.Length == 0 ? "General meeting" : Client;
    [JsonIgnore] public bool IsValid => Client is not null && Project is not null && Goal is not null
        && Client.Length <= 120 && Project.Length <= 120 && Goal.Length <= 2000
        && (ProfileId == Guid.Empty ? Client.Length == 0 : !string.IsNullOrWhiteSpace(Client))
        && Participants is { Length: <= 50 } && Participants.All(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 100)
        && Vocabulary is { Length: <= 100 } && Vocabulary.All(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 80)
        && Client.Length + Project.Length + Goal.Length + Participants.Sum(p => p.Length) + Vocabulary.Sum(v => v.Length) <= 6000;
    public string PromptBrief()
    {
        if (!IsValid) throw new InvalidDataException("Check the client, project, meeting goal, participant names and vocabulary limits.");
        var lines = new List<string>();
        if (Client.Length > 0) lines.Add("Client: " + Client);
        if (Project.Length > 0) lines.Add("Project: " + Project);
        if (Goal.Length > 0) lines.Add("Meeting goal: " + Goal);
        if (Participants.Length > 0) lines.Add("Expected participants (not confirmed speaker identities): " + string.Join(", ", Participants));
        if (Vocabulary.Length > 0) lines.Add("Work vocabulary: " + string.Join(", ", Vocabulary));
        return lines.Count == 0 ? "" : "Meeting brief supplied by the user:\n" + string.Join("\n", lines);
    }
    public string CombineWith(string notes)
    {
        var brief = PromptBrief(); var result = brief.Length == 0 ? notes : notes.Length == 0 ? brief : notes + "\n\n" + brief;
        if (result.Length > 16000) throw new InvalidDataException("Meeting brief and AI context notes together exceed 16,000 characters. Shorten them before starting.");
        return result;
    }
    public SessionContext Snapshot() => this with { Participants = [.. Participants], Vocabulary = [.. Vocabulary] };
}
public sealed record SessionContextBook
{
    public int SchemaVersion { get; init; } = 1;
    public Guid SelectedProfileId { get; init; }
    public SessionContext[] Profiles { get; init; } = [new()];
}
public sealed record RecordedSessionContext(int SchemaVersion, Guid SessionId, SessionContext Context);
public sealed class SessionContextStore(string directory)
{
    public string FilePath => Path.Combine(directory, "client-contexts.json");
    public SessionContextBook Load()
    {
        var book = File.Exists(FilePath) ? JsonSerializer.Deserialize<SessionContextBook>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Empty client contexts.") : new();
        Validate(book); return book;
    }
    public static void Validate(SessionContextBook book)
    {
        if (book.SchemaVersion != 1 || book.Profiles is not { Length: >= 1 and <= 201 } || book.Profiles.Any(p => p is null || !p.IsValid)
            || book.Profiles.Select(p => p.ProfileId).Distinct().Count() != book.Profiles.Length
            || book.Profiles.Select(p => p.Client).Distinct(StringComparer.OrdinalIgnoreCase).Count() != book.Profiles.Length
            || !book.Profiles.Any(p => p.ProfileId == Guid.Empty) || !book.Profiles.Any(p => p.ProfileId == book.SelectedProfileId))
            throw new InvalidDataException("Client profiles must have distinct names and IDs, a general meeting profile and a valid selection.");
    }
    public SessionContextBook SaveProfile(SessionContextBook book, SessionContext context)
    {
        var next = book with { SelectedProfileId = context.ProfileId, Profiles = [.. book.Profiles.Where(p => p.ProfileId != context.ProfileId), context.Snapshot()] };
        Save(next); return next;
    }
    public void Save(SessionContextBook book) { Validate(book); Write(FilePath, book); }
    public static void SaveSnapshot(string recordingDirectory, Guid sessionId, SessionContext context)
    {
        if (!context.IsValid || sessionId == Guid.Empty || !Directory.Exists(recordingDirectory)) throw new InvalidDataException("A valid recording and meeting brief are required.");
        Write(Path.Combine(recordingDirectory, "context.json"), new RecordedSessionContext(1, sessionId, context.Snapshot()));
    }
    public static RecordedSessionContext? ReadSnapshot(string recordingDirectory)
    {
        var path = Path.Combine(recordingDirectory, "context.json");
        if (!File.Exists(path)) return null;
        var saved = JsonSerializer.Deserialize<RecordedSessionContext>(File.ReadAllText(path));
        return saved is { SchemaVersion: 1, Context.IsValid: true } && saved.SessionId != Guid.Empty ? saved : throw new InvalidDataException("Invalid meeting context snapshot.");
    }
    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
