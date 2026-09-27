using System.IO;
using System.Text.Json;

namespace SecondBrain.App;

internal sealed class VideoRecording
{
    private readonly object gate = new();
    private readonly Queue<DisplayFrame> frames = new();
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
        Func<DisplayChoice, Action<DisplayFrame>, Action<string>, IDisplayCapture>? factory = null)
    {
        var size = selection.Size;
        Path = System.IO.Path.Combine(directory, "video-" + Guid.NewGuid().ToString("N") + ".mp4");
        worker = Task.Factory.StartNew(() => Run(selection, size, audioOrigin, factory), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private void Observe(DisplayFrame frame)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) return;
            if (frames.Count == 4) { Array.Clear(frames.Dequeue().Pixels); dropped++; }
            frames.Enqueue(frame with { Pixels = frame.Pixels.ToArray() });
        }
    }
    private void Fail(string reason) { lock (gate) failure ??= reason; stop.Cancel(); }
    private void Run(VideoSelection selection, (int Width, int Height) size, double audioOrigin, Func<DisplayChoice, Action<DisplayFrame>, Action<string>, IDisplayCapture>? factory)
    {
        DisplayFrame? current = null; var began = AudioClock.Now; var scaled = new byte[size.Width * size.Height * 4];
        try
        {
            using var writer = new NativeVideoWriter(Path, size.Width, size.Height);
            Save("Recording", size, audioOrigin);
            capture = (factory ?? ((d, receive, fail) => new DisplayVideoCapture(d, receive, fail)))(selection.Display, Observe, Fail);
            while (!stop.IsCancellationRequested)
            {
                if (current is null)
                {
                    lock (gate) if (frames.Count > 0) { current = frames.Dequeue(); Origin = current.Clock; }
                    if (current is null) { if (AudioClock.Now - began > 6) throw new IOException("No display frames arrived."); stop.Token.WaitHandle.WaitOne(5); continue; }
                    Scale(current, scaled, size.Width, size.Height);
                }
                // Use the compositor clock; leave a small arrival allowance instead of timestamping CPU copy completion.
                var due = (long)Math.Floor((AudioClock.Now - Origin - .1) * 30);
                if (due - FrameCount > 15) throw new IOException("Video encoding cannot keep up. Try 1080p recording.");
                if (due < FrameCount) { stop.Token.WaitHandle.WaitOne(5); continue; }
                var at = Origin + FrameCount / 30d; DisplayFrame? newer = null;
                lock (gate)
                {
                    while (frames.TryPeek(out var next) && next.Clock <= at) { if (newer is not null) Array.Clear(newer.Pixels); newer = frames.Dequeue(); }
                }
                if (newer is not null) { Array.Clear(current.Pixels); current = newer; Scale(current, scaled, size.Width, size.Height); }
                writer.Write(scaled, FrameCount++); ready.TrySetResult();
            }
            if (FrameCount > 0) writer.Complete();
            else if (failure is null) failure = "Stopped before the first video frame.";
        }
        catch (Exception ex) { failure ??= ex is IOException or InvalidOperationException ? ex.Message : "Native video encoding failed (" + ex.GetType().Name + ")."; }
        finally
        {
            capture?.Dispose(); capture = null;
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
            Frames = FrameCount, SessionStartSeconds = Origin == 0 ? (double?)null : Origin - audioOrigin, DurationSeconds = FrameCount / 30d, DroppedCaptureFrames = dropped, Audio = false, Error = failure }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, file, true);
    }
    internal async Task Stop() { stop.Cancel(); await worker; }
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
