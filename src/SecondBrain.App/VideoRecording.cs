using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class VideoRecording
{
    private readonly object gate = new();
    private readonly Queue<DisplayFrame> frames = new();
    private readonly Channel<(AudioPacket Packet, int Rate)> audioPackets = Channel.CreateBounded<(AudioPacket, int)>(128);
    private VideoAudioMixer? mixer;
    private double stopAt;
    private long audioPacketsReceived;
    internal long AudioPacketsReceived => Interlocked.Read(ref audioPacketsReceived);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private IDisplayCapture? capture;
    private string? failure;
    private int dropped;
    internal string Path { get; }
    internal string? Error => failure;
    internal bool Active => !worker.IsCompleted;
    internal long FrameCount { get; private set; }
    internal double Origin { get; private set; }
    internal Task Ready => ready.Task;
    internal VideoRecording(string directory, VideoSelection selection, double audioOrigin,
        Func<DisplayChoice, Action<DisplayFrame>, Action<string>, IDisplayCapture>? factory = null, RecordingService? audio = null, Func<long>? freeBytes = null, Action<long>? beforeFrame = null)
    {
        var size = selection.Size;
        Path = System.IO.Path.Combine(directory, "video-" + Guid.NewGuid().ToString("N") + ".mp4");
        worker = Task.Factory.StartNew(() => Run(selection, size, audioOrigin, factory, audio, freeBytes, beforeFrame), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private void Observe(DisplayFrame frame)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || Volatile.Read(ref stopAt) is > 0 and var cutoff && frame.Clock > cutoff) return;
            if (frames.Count == 4) { Array.Clear(frames.Dequeue().Pixels); dropped++; }
            frames.Enqueue(frame with { Pixels = frame.Pixels.ToArray() });
        }
    }
    private void Audio(AudioPacket packet, int rate)
    {
        if (stop.IsCancellationRequested) return;
        if (!audioPackets.Writer.TryWrite((packet, rate))) Fail("Video audio queue could not keep up. Listening continues.");
        else Interlocked.Increment(ref audioPacketsReceived);
    }
    private void Fail(string reason) { lock (gate) failure ??= reason; stop.Cancel(); }
    private void Run(VideoSelection selection, (int Width, int Height) size, double audioOrigin, Func<DisplayChoice, Action<DisplayFrame>, Action<string>, IDisplayCapture>? factory, RecordingService? audio, Func<long>? freeBytes, Action<long>? beforeFrame)
    {
        DisplayFrame? current = null; var began = AudioClock.Now; var scaled = new byte[size.Width * size.Height * 4];
        try
        {
            using var lease = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            freeBytes ??= () => new DriveInfo(System.IO.Path.GetPathRoot(Path)!).AvailableFreeSpace;
            if (freeBytes() < 512L * 1024 * 1024) throw new IOException("Video needs at least 512 MB free. Free space or choose another data drive.");
            using var writer = new NativeVideoWriter(Path, size.Width, size.Height, audio is not null);
            try
            {
            Save("Recording", size, audioOrigin);
            if (audio is not null) audio.AudioForVideo += Audio;
            capture = (factory ?? ((d, receive, fail) => new DisplayVideoCapture(d, receive, fail)))(selection.Display, Observe, Fail);
            while (true)
            {
                var stopping = stop.IsCancellationRequested;
                if (stopping && (failure is not null || current is null)) break;
                if (current is null)
                {
                    lock (gate) if (frames.Count > 0) { current = frames.Dequeue(); Origin = current.Clock; }
                    if (current is null) { if (AudioClock.Now - began > 6) throw new IOException("No display frames arrived."); stop.Token.WaitHandle.WaitOne(5); continue; }
                    Scale(current, scaled, size.Width, size.Height);
                    if (audio is not null) mixer = new(Origin - audioOrigin);
                }
                // Use the compositor clock; leave a small arrival allowance instead of timestamping CPU copy completion.
                var end = Volatile.Read(ref stopAt);
                var due = stopping ? (long)Math.Ceiling((end - Origin) * 30) - 1 : (long)Math.Floor((AudioClock.Now - Origin - .1) * 30);
                if (end > 0) due = Math.Min(due, (long)Math.Ceiling((end - Origin) * 30) - 1);
                if (stopping && FrameCount > due) break;
                if (due - FrameCount > 15) throw new IOException("Video encoding cannot keep up. Try 1080p recording.");
                if (due < FrameCount) { stop.Token.WaitHandle.WaitOne(5); continue; }
                var at = Origin + FrameCount / 30d; DisplayFrame? newer = null;
                lock (gate)
                {
                    while (frames.TryPeek(out var next) && next.Clock <= at) { if (newer is not null) Array.Clear(newer.Pixels); newer = frames.Dequeue(); }
                }
                if (newer is not null) { Array.Clear(current.Pixels); current = newer; Scale(current, scaled, size.Width, size.Height); }
                if (mixer is not null)
                {
                    while (audioPackets.Reader.TryRead(out var packet)) mixer.Offer(packet.Packet, packet.Rate);
                    var start = mixer.Through; writer.WriteAudio(mixer.Read(1600), start);
                }
                if (FrameCount % 30 == 0 && freeBytes() < 128L * 1024 * 1024) { Fail("Low disk space: video stopped with a reserve for audio and metadata. Free space soon."); break; }
                beforeFrame?.Invoke(FrameCount);
                writer.Write(scaled, FrameCount); FrameCount++; ready.TrySetResult();
                if (FrameCount == 1 || FrameCount % 60 == 0) Save("Recording", size, audioOrigin);
            }
            if (FrameCount == 0 && failure is null) failure = "Stopped before the first video frame.";
            }
            catch (Exception ex) { failure ??= ex is IOException or InvalidOperationException ? ex.Message : "Native encoding failed (" + ex.GetType().Name + ")."; }
            finally
            {
                if (FrameCount > 0) try { writer.Complete(); } catch (Exception) { failure ??= "Video could not finalize. Recover its complete fragments from Meetings."; }
            }
        }
        catch (Exception ex) { failure ??= ex is IOException or InvalidOperationException ? ex.Message : "Native video encoding failed (" + ex.GetType().Name + ")."; }
        finally
        {
            if (audio is not null) audio.AudioForVideo -= Audio;
            try { capture?.Dispose(); } catch (Exception) { failure ??= "Display capture could not close cleanly."; } capture = null;
            while (audioPackets.Reader.TryRead(out _)) { }
            if (current is not null) Array.Clear(current.Pixels); Array.Clear(scaled);
            lock (gate) while (frames.Count > 0) Array.Clear(frames.Dequeue().Pixels);
            try { Save(failure is null ? "Completed" : "Failed", size, audioOrigin); } catch (Exception) { failure ??= "Video metadata could not be saved."; }
            if (failure is not null) ready.TrySetException(new IOException(failure)); else ready.TrySetResult();
        }
    }
    private void Save(string state, (int Width, int Height) size, double audioOrigin)
    {
        var file = System.IO.Path.ChangeExtension(Path, ".json"); var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { Schema = 1, State = state, File = System.IO.Path.GetFileName(Path), size.Width, size.Height, FramesPerSecond = 30,
            Frames = FrameCount, SessionStartSeconds = Origin == 0 ? (double?)null : Origin - audioOrigin, DurationSeconds = FrameCount / 30d, DroppedCaptureFrames = dropped, Audio = mixer is not null, AudioSampleRate = mixer is null ? 0 : VideoAudioMixer.SampleRate,
            AudioPackets = AudioPacketsReceived, AudioFrames = mixer?.Through ?? 0, LateAudioSamples = mixer?.LateSamples ?? 0,
            MissingMicrophoneSamples = mixer?.MissingMicrophoneSamples ?? 0, MissingSystemSamples = mixer?.MissingSystemSamples ?? 0,
            EstimatedAudioPackets = mixer?.EstimatedPackets ?? 0, AudioDiscontinuities = mixer?.Discontinuities ?? 0,
            AudioMix = "Equal-gain microphone and system PCM, resampled to 48 kHz mono; missing packets are padded, not proof of captured silence", Error = failure }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, file, true);
    }
    internal void EndAt(double clock) => Interlocked.CompareExchange(ref stopAt, clock, 0);
    internal async Task Stop() { EndAt(AudioClock.Now); stop.Cancel(); await worker; }
    private static void Scale(DisplayFrame source, byte[] output, int width, int height)
    {
        if (source.Width == width && source.Height == height) { source.Pixels.CopyTo(output, 0); return; }
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var from = ((long)y * source.Height / height * source.Width + (long)x * source.Width / width) * 4; var to = (y * width + x) * 4;
            output[to] = source.Pixels[from]; output[to + 1] = source.Pixels[from + 1]; output[to + 2] = source.Pixels[from + 2]; output[to + 3] = 255;
        }
    }
}
