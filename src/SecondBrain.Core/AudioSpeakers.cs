using System.Text;
using System.Text.Json;

namespace SecondBrain.Core;

public sealed record SpeakerOptions(bool SeparateSpeakers = true, string LocalParticipant = "")
{
    public bool IsValid => LocalParticipant is not null && LocalParticipant.Length <= 100 && !LocalParticipant.Any(char.IsControl);
}
public sealed class SpeakerSettings(string directory)
{
    private readonly string path = Path.Combine(directory, "speaker-options.json");
    public SpeakerOptions Load()
    {
        var value = File.Exists(path) ? JsonSerializer.Deserialize<SpeakerOptions>(File.ReadAllText(path)) : new();
        return value is { IsValid: true } ? value : throw new InvalidDataException("Invalid speaker settings.");
    }
    public void Save(SpeakerOptions value)
    {
        if (!value.IsValid) throw new InvalidDataException("Use a microphone participant name of at most 100 characters.");
        Directory.CreateDirectory(directory);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
public static class StreamingSpeechOptions
{
    public static string[] Keyterms(IEnumerable<string> values)
    {
        var result = new List<string>(); var bytes = 0;
        foreach (var term in values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var size = Encoding.UTF8.GetByteCount(term) + 1;
            // Conservative byte budget stays below the provider's 500-token limit, including separators.
            if (term.Length > 80 || result.Count >= 100 || bytes + size > 400) continue;
            result.Add(term); bytes += size;
        }
        return result.ToArray();
    }
    public static Uri Uri(int sampleRate, bool diarize, IEnumerable<string> vocabulary)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var query = $"wss://api.deepgram.com/v1/listen?model=nova-3&language=en&encoding=linear16&sample_rate={sampleRate}&channels=1&interim_results=true&endpointing=300&punctuate=true";
        if (diarize) query += "&diarize_model=v1";
        return new(query + string.Concat(Keyterms(vocabulary).Select(term => "&keyterm=" + System.Uri.EscapeDataString(term))));
    }
}

// Provider speaker numbers are only meaningful inside their connection. No voiceprints or cross-session identity inference.
public sealed class AudioSpeakers(Guid sessionId, SpeakerOptions options)
{
    private readonly Dictionary<(Guid Connection, int Speaker), int> labels = [];
    private int next;
    public TranscriptDetail Label(TranscriptDetail detail)
    {
        if (detail.SessionId != sessionId || !options.IsValid) throw new InvalidDataException("Speaker context does not match this recording.");
        var words = detail.Words;
        var mapped = words.Select((word, i) =>
        {
            string? id = null; var name = "Unknown";
            if (detail.Source == AudioSource.Microphone && !string.IsNullOrWhiteSpace(options.LocalParticipant))
            { id = sessionId.ToString("N") + ":microphone"; name = options.LocalParticipant.Trim(); }
            else if (detail.Source == AudioSource.System && options.SeparateSpeakers && detail.ConnectionId != Guid.Empty
                && word.ProviderSpeaker is { } speaker && speaker >= 0 && word.Confidence is not < .5f && word.SpeakerConfidence is not < .6f)
            {
                var overlap = words.Where((_, j) => j != i).Any(other => other.ProviderSpeaker is { } otherSpeaker && otherSpeaker != speaker
                    && Math.Min(other.End, word.End) - Math.Max(other.Start, word.Start) > .03);
                if (!overlap)
                {
                    var key = (detail.ConnectionId, speaker);
                    if (labels.TryGetValue(key, out var label) || labels.Count < 4096)
                    {
                        if (label == 0) labels[key] = label = ++next;
                        id = sessionId.ToString("N") + ":remote:" + label; name = "Speaker " + label;
                    }
                }
            }
            return word with { SpeakerId = id, SpeakerLabel = name };
        }).ToArray();
        return detail with { Words = mapped, MetadataStatus = detail.MetadataStatus + " · audio labels are provisional" };
    }
}
