using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public enum AudioSource { Microphone, System }
public sealed record AudioTrack(AudioSource Source, string DeviceId, string Name, int SampleRate);
public sealed record AudioPacket(AudioSource Source, double Seconds, byte[] Mono16, bool Silent = false, bool Discontinuity = false, bool EstimatedTime = false);
public sealed record AudioChunk(AudioSource Source, string File, long StartFrame, long Frames);
public sealed record RecordingEvent(double Seconds, string Kind, AudioSource? Source = null, double? Until = null);
public sealed class RecordingManifest
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string State { get; set; } = "Recording";
    public double DurationSeconds { get; set; }
    public string Clock { get; init; } = "WASAPI packet QPC timestamps, common session origin; native sample rates; PCM16 mono";
    public string Padding { get; init; } = "Uncaptured intervals and pauses are zero-padded in exported tracks; see events. Padding is not proof of captured silence.";
    public List<AudioTrack> Tracks { get; init; } = [];
    public List<AudioChunk> Chunks { get; set; } = [];
    public List<RecordingEvent> Events { get; init; } = [];
    public Dictionary<AudioSource, long> CapturedFrames { get; init; } = [];
    public Dictionary<AudioSource, long> SilentFrames { get; init; } = [];
}

// One storage worker owns this object. Capture threads never perform file I/O.
public sealed class RecordingSession : IDisposable
{
    private sealed class TrackWriter(AudioTrack track)
    {
        public AudioTrack Track = track;
        public PcmWaveFile? Wave;
        public long Start, Through;
        public string? Partial;
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<AudioSource, TrackWriter> writers;
    private readonly FileStream sessionLock;
    private double checkpoint;
    public string DirectoryPath { get; }
    public RecordingManifest Manifest { get; }
    public RecordingSession(string root, IReadOnlyList<AudioTrack> tracks, DateTimeOffset? startedUtc = null)
    {
        if (tracks.Count != 2 || tracks.Select(t => t.Source).Distinct().Count() != 2 || tracks.Any(t => t.SampleRate is < 8000 or > 192000))
            throw new ArgumentException("Two distinct tracks with supported sample rates are required.");
        DirectoryPath = Path.Combine(root, DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        sessionLock = new(Path.Combine(DirectoryPath, "recording.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Manifest = new() { StartedUtc = startedUtc ?? DateTimeOffset.UtcNow, Tracks = tracks.ToList(), Events = [new(0, "Start")] };
        writers = tracks.ToDictionary(t => t.Source, t => new TrackWriter(t));
        try { Save(); } catch { sessionLock.Dispose(); throw; }
    }
    public void Write(AudioPacket packet)
    {
        if (!double.IsFinite(packet.Seconds) || packet.Seconds < -1 || packet.Seconds > 12 * 3600 || packet.Mono16.Length % 2 != 0) throw new InvalidDataException("Invalid recording timestamp or PCM packet.");
        var writer = writers[packet.Source]; var rate = writer.Track.SampleRate;
        var start = (long)Math.Round(packet.Seconds * rate);
        var skip = (int)Math.Min(packet.Mono16.Length / 2, Math.Max(0, Math.Max(writer.Through, 0) - start));
        start += skip;
        var data = packet.Mono16.AsSpan(skip * 2);
        if (data.Length == 0) return;
        Manifest.CapturedFrames[packet.Source] = Manifest.CapturedFrames.GetValueOrDefault(packet.Source) + data.Length / 2;
        if (packet.Silent) Manifest.SilentFrames[packet.Source] = Manifest.SilentFrames.GetValueOrDefault(packet.Source) + data.Length / 2;
        if (start > writer.Through + rate / 20) Manifest.Events.Add(new(writer.Through / (double)rate, "NoCapturedPackets", packet.Source, start / (double)rate));
        if (packet.Discontinuity) Manifest.Events.Add(new(packet.Seconds, "DeviceDiscontinuity", packet.Source));
        if (packet.EstimatedTime && !Manifest.Events.Any(e => e.Kind == "EstimatedTimestamp" && e.Source == packet.Source))
            Manifest.Events.Add(new(packet.Seconds, "EstimatedTimestamp", packet.Source));
        while (data.Length > 0)
        {
            if (writer.Wave is null || start >= writer.Start + rate * 10L)
            {
                FinishChunk(writer); writer.Start = start;
                writer.Partial = $"{packet.Source.ToString().ToLowerInvariant()}-{start:D12}.wav.part";
                writer.Wave = new(Path.Combine(DirectoryPath, writer.Partial), rate);
            }
            var chunkStart = writer.Start;
            writer.Wave.PadTo(start - chunkStart);
            var count = (int)Math.Min(data.Length / 2, chunkStart + rate * 10L - start);
            writer.Wave.Write(data[..(count * 2)]); data = data[(count * 2)..]; start += count;
        }
        writer.Through = start;
        Manifest.DurationSeconds = Math.Max(Manifest.DurationSeconds, start / (double)rate);
        if (Manifest.DurationSeconds - checkpoint >= 1)
        {
            foreach (var item in writers.Values) item.Wave?.Checkpoint();
            checkpoint = Manifest.DurationSeconds; Save();
        }
    }
    public void Mark(string state, double seconds, string? reason = null)
    {
        foreach (var writer in writers.Values) FinishChunk(writer);
        Manifest.DurationSeconds = Math.Max(Manifest.DurationSeconds, Math.Max(0, seconds));
        Manifest.State = state; Manifest.Events.Add(new(seconds, reason ?? state)); Save();
    }
    public void Complete(double seconds)
    {
        Mark("Finalizing", seconds);
        Export(DirectoryPath, Manifest);
        Manifest.State = "Completed"; Save();
    }
    private void FinishChunk(TrackWriter writer)
    {
        if (writer.Wave is null) return;
        var wave = writer.Wave; var file = writer.Partial![..^5];
        var frames = wave.Frames; wave.Dispose(); writer.Wave = null;
        File.Move(Path.Combine(DirectoryPath, writer.Partial), Path.Combine(DirectoryPath, file));
        Manifest.Chunks.Add(new(writer.Track.Source, file, writer.Start, frames));
    }
    private void Save() => SaveManifest(DirectoryPath, Manifest);
    private static void SaveManifest(string directory, RecordingManifest manifest)
    {
        var path = Path.Combine(directory, "session.json");
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, manifest, Json); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public static RecordingManifest ReadManifest(string directory)
    {
        var manifest = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(Path.Combine(directory, "session.json")))
            ?? throw new InvalidDataException("Missing session manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Tracks is null || manifest.Chunks is null || manifest.Events is null
            || manifest.Tracks.Count != 2 || manifest.Tracks.Any(t => t is null || !Enum.IsDefined(t.Source)) || manifest.Tracks.Select(t => t.Source).Distinct().Count() != 2
            || manifest.Tracks.Any(t => t.SampleRate is < 8000 or > 192000) || !double.IsFinite(manifest.DurationSeconds) || manifest.DurationSeconds is < 0 or > 43200)
            throw new InvalidDataException("Unsupported session manifest.");
        return manifest;
    }
    public static RecordingManifest Recover(string directory)
    {
        using var guard = new FileStream(Path.Combine(directory, "recording.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = ReadManifest(directory);
        if (manifest.State is "Completed" or "Recovered") return manifest;
        // Enumerate only our own canonical chunk names. Never follow manifest-provided paths.
        manifest.Chunks.Clear();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var match = Regex.Match(Path.GetFileName(path), @"^(microphone|system)-(\d{12})\.wav(\.part)?$");
            if (!match.Success) continue;
            var source = match.Groups[1].Value == "microphone" ? AudioSource.Microphone : AudioSource.System;
            var rate = manifest.Tracks.Single(t => t.Source == source).SampleRate;
            var start = long.Parse(match.Groups[2].Value);
            if (start > rate * 43200L) throw new InvalidDataException("Chunk timestamp is out of range.");
            var frames = PcmWaveFile.Repair(path, rate);
            var complete = path.EndsWith(".part", StringComparison.Ordinal) ? path[..^5] : path;
            if (path != complete) File.Move(path, complete); // Conflicts remain visible; never replace a chunk.
            manifest.Chunks.Add(new(source, Path.GetFileName(complete), start, frames));
            manifest.DurationSeconds = Math.Max(manifest.DurationSeconds, (start + frames) / (double)rate);
        }
        manifest.Events.Add(new(manifest.DurationSeconds, "InterruptedSessionRecovered"));
        manifest.State = "Recovering"; SaveManifest(directory, manifest);
        Export(directory, manifest); manifest.State = "Recovered"; SaveManifest(directory, manifest); return manifest;
    }
    private static void Export(string directory, RecordingManifest manifest)
    {
        foreach (var track in manifest.Tracks)
        {
            var path = Path.Combine(directory, track.Source.ToString().ToLowerInvariant() + ".wav");
            using (var output = new PcmWaveFile(path + ".exporting", track.SampleRate, overwrite: true))
            {
                foreach (var chunk in manifest.Chunks.Where(c => c.Source == track.Source).OrderBy(c => c.StartFrame))
                {
                    output.PadTo(chunk.StartFrame);
                    using var input = File.OpenRead(Path.Combine(directory, chunk.File)); input.Position = 44;
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer)) > 0) output.Write(buffer.AsSpan(0, count));
                }
                output.PadTo((long)Math.Ceiling(manifest.DurationSeconds * track.SampleRate));
            }
            File.Move(path + ".exporting", path, true);
        }
    }
    public void Dispose()
    {
        // A failed session keeps .part files; recovery rebuilds headers from actual PCM bytes.
        foreach (var writer in writers.Values) { try { writer.Wave?.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { } }
        sessionLock.Dispose();
    }
}

public sealed class PcmWaveFile : IDisposable
{
    private readonly FileStream file;
    private readonly int rate;
    private bool disposed;
    public long Frames => (file.Length - 44) / 2;
    public PcmWaveFile(string path, int sampleRate, bool overwrite = false)
    {
        rate = sampleRate; file = new(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try { file.Write(new byte[44]); Checkpoint(); } catch { file.Dispose(); throw; }
    }
    public void Write(ReadOnlySpan<byte> pcm) { file.Position = file.Length; file.Write(pcm); }
    public void PadTo(long frame)
    {
        var missing = frame - Frames; if (missing <= 0) return;
        var zeros = new byte[32768];
        while (missing > 0) { var count = (int)Math.Min(missing, zeros.Length / 2); Write(zeros.AsSpan(0, count * 2)); missing -= count; }
    }
    private static byte[] Header(long dataBytes, int rate)
    {
        if (dataBytes > uint.MaxValue - 36 || dataBytes < 0) throw new IOException("WAV size limit reached.");
        var h = new byte[44]; "RIFF"u8.CopyTo(h); "WAVEfmt "u8.CopyTo(h.AsSpan(8)); "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)dataBytes + 36);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes); return h;
    }
    public void Checkpoint() { file.Position = 0; file.Write(Header(file.Length - 44, rate)); file.Flush(true); }
    public static long Repair(string path, int rate)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length < 44 || stream.Length > 44 + rate * 20L) throw new InvalidDataException("Invalid audio chunk length.");
        var frames = (stream.Length - 44) / 2; stream.SetLength(44 + frames * 2); stream.Position = 0;
        stream.Write(Header(frames * 2, rate)); stream.Flush(true); return frames;
    }
    public void Dispose() { if (disposed) return; disposed = true; try { Checkpoint(); } finally { file.Dispose(); } }
}
