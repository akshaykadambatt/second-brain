using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondBrain.Core;

// Deepgram time counts submitted samples, not wall time. Loopback silence and
// reconnection therefore require a piecewise mapping back to the recording clock.
public sealed class TranscriptTimeline(int sampleRate)
{
    private sealed record Span(double StreamStart, double SessionStart, double Duration);
    private readonly List<Span> spans = [];
    private readonly object gate = new();
    private long frames;
    public double SentSeconds { get { lock (gate) return frames / (double)sampleRate; } }
    public void Add(double sessionSeconds, int sampleCount)
    {
        if (!double.IsFinite(sessionSeconds) || sampleCount <= 0 || sampleRate <= 0) throw new InvalidDataException("Invalid audio timing.");
        lock (gate)
        {
            var start = frames / (double)sampleRate; var duration = sampleCount / (double)sampleRate;
            if (spans.Count > 0 && Math.Abs(spans[^1].SessionStart + spans[^1].Duration - sessionSeconds) < .002)
                spans[^1] = spans[^1] with { Duration = spans[^1].Duration + duration };
            else
            {
                if (spans.Count >= 100_000) throw new InvalidDataException("Audio timing map is full; reconnect required.");
                spans.Add(new(start, sessionSeconds, duration));
            }
            frames += sampleCount;
        }
    }
    public double Map(double seconds, bool end = false)
    {
        lock (gate)
        {
            if (spans.Count == 0 || !double.IsFinite(seconds) || seconds < 0 || seconds > frames / (double)sampleRate + .1)
                throw new InvalidDataException("Transcript time is outside submitted audio.");
            // At a silence boundary, an end belongs to the preceding packet;
            // the next segment's start belongs to the following packet.
            var lo = 0; var hi = spans.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (spans[mid].StreamStart < seconds || (!end && spans[mid].StreamStart == seconds)) lo = mid; else hi = mid - 1;
            }
            var span = spans[lo];
            return Math.Max(0, span.SessionStart + Math.Clamp(seconds - span.StreamStart, 0, span.Duration));
        }
    }
}

public sealed record TranscriptEntry(Guid SessionId, string Kind, AudioSource? Source, double Start, double End,
    string Text, Guid ConnectionId = default, string? Id = null);

// The journal is authoritative. Each complete JSON line is flushed before it is
// shown as saved. An interrupted final line is removed on reopen, never guessed.
public sealed class TranscriptLog : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    private readonly FileStream file;
    private readonly HashSet<string> ids = [];
    private bool failed;
    public string DirectoryPath { get; }
    public Guid SessionId { get; }
    public TranscriptLog(string directory, Guid sessionId)
    {
        DirectoryPath = directory; SessionId = sessionId;
        var path = Path.Combine(directory, "transcript.jsonl");
        file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            // Drop only an incomplete trailing line. Corruption of a complete
            // record refuses further writes instead of hiding damaged history.
            if (file.Length > 0)
            {
                var length = file.Length;
                file.Position = length - 1;
                if (file.ReadByte() != '\n')
                {
                    var pos = length - 1;
                    while (pos >= 0) { file.Position = pos; if (file.ReadByte() == '\n') break; pos--; }
                    file.SetLength(pos + 1); file.Flush(true);
                }
                file.Position = 0;
                using var reader = new StreamReader(file, Encoding.UTF8, false, 4096, true);
                while (reader.ReadLine() is { } line)
                {
                    var entry = JsonSerializer.Deserialize<TranscriptEntry>(line, Json) ?? throw new InvalidDataException("Invalid transcript record.");
                    if (entry.SessionId != SessionId) throw new InvalidDataException("Transcript belongs to another session.");
                    if (entry.Id is not null) ids.Add(entry.Id);
                }
            }
            file.Position = file.Length;
        }
        catch { file.Dispose(); throw; }
    }
    public bool Append(TranscriptEntry entry)
    {
        if (entry.SessionId != SessionId || !double.IsFinite(entry.Start) || !double.IsFinite(entry.End) || entry.Start < 0 || entry.End < entry.Start)
            throw new InvalidDataException("Invalid transcript provenance.");
        lock (gate)
        {
            if (failed) throw new IOException("Transcript writer previously failed; recover before further writes.");
            if (entry.Id is not null && ids.Contains(entry.Id)) return false;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
            try { file.Write(bytes); file.Flush(true); }
            catch { failed = true; throw; }
            if (entry.Id is not null) ids.Add(entry.Id);
            return true;
        }
    }
    public static TranscriptEntry[] Read(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, "transcript.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var entries = new List<TranscriptEntry>();
        while (reader.ReadLine() is { } line) entries.Add(JsonSerializer.Deserialize<TranscriptEntry>(line, Json) ?? throw new InvalidDataException("Invalid transcript record."));
        return entries.ToArray();
    }
    public static void Recover(string directory, RecordingManifest manifest)
    {
        if (!File.Exists(Path.Combine(directory, "transcript.jsonl"))) return;
        if (File.Exists(Path.Combine(directory, TranscriptDetails.FileName)))
        { using var details = new TranscriptDetails(directory, manifest.Id); }
        using var journal = new TranscriptLog(directory, manifest.Id);
        var records = Read(directory);
        if (records.Count(e => e.Kind == "RunStart") > records.Count(e => e.Kind == "RunEnd"))
        {
            foreach (var source in new[] { AudioSource.Microphone, AudioSource.System })
            {
                var start = records.Where(e => e.Source == source && e.Kind == "Final").Select(e => e.End).DefaultIfEmpty(0).Max();
                journal.Append(new(manifest.Id, "Gap", source, start, Math.Max(start, manifest.DurationSeconds), "Interrupted session recovered; tail may not have been transcribed.", Id: "recovery-" + source));
            }
            journal.Append(new(manifest.Id, "RunEnd", null, manifest.DurationSeconds, manifest.DurationSeconds, "Recovered after interruption; no new cloud transcription performed.", Id: "recovery-end"));
        }
        journal.ExportMarkdown();
    }
    public void ExportMarkdown()
    {
        lock (gate)
        {
            var temporary = Path.Combine(DirectoryPath, "transcript.md.tmp");
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# Meeting transcript\n\nSession: " + SessionId + "\n\nMachine transcription. Only finalized segments are saved. Gap intervals indicate possible missing words; silence is not proof of transcript completeness. Times use the recording session clock. An unmatched RunStart means transcription was interrupted.\n");
                foreach (var entry in Read(DirectoryPath))
                {
                    var label = entry.Source?.ToString() ?? "Session";
                    var time = $"{TimeSpan.FromSeconds(entry.Start):hh\\:mm\\:ss\\.fff}–{TimeSpan.FromSeconds(entry.End):hh\\:mm\\:ss\\.fff}";
                    if (entry.Kind == "Final") writer.WriteLine($"**{label} · {time}** ([audio]({(entry.Source == AudioSource.Microphone ? "microphone" : "system")}.wav#t={entry.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}))\n\n{entry.Text.Replace("\n", " ", StringComparison.Ordinal)}\n");
                    else writer.WriteLine($"> {label} · {time} · {entry.Kind}: {entry.Text}\n");
                }
            }
            File.Move(temporary, Path.Combine(DirectoryPath, "transcript.md"), true);
        }
    }
    public void Dispose() { lock (gate) file.Dispose(); }
}
