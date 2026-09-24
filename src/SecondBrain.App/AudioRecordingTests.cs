using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using NAudio.Wave;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class AudioRecordingTests
{
    internal sealed class SyntheticSource(AudioSource source, string id) : IAudioCaptureSource
    {
        public static int OpenCount;
        public AudioTrack Track { get; } = new(source, id, "Synthetic " + source, source == AudioSource.Microphone ? 16000 : 48000);
        private double next;
        private bool started, disposed;
        public void Start() { started = true; next = AudioClock.Now; Interlocked.Increment(ref OpenCount); }
        public void Drain(Action<CapturedAudio> received)
        {
            var now = AudioClock.Now;
            while (next + .02 <= now)
            {
                var bytes = new byte[Track.SampleRate / 50 * 2];
                for (var i = 0; i < bytes.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(7000 * Math.Sin((next + i / (double)Track.SampleRate) * Math.PI * 440)));
                received(new(next, bytes, 75, false, false, false)); next += .02;
            }
        }
        public void Dispose() { if (disposed) return; disposed = true; if (started) Interlocked.Decrement(ref OpenCount); }
    }
    public static IAudioCaptureSource CreateSource(AudioSource source, string id) => new SyntheticSource(source, id);
    private sealed class DisconnectingSource(AudioSource source, string id) : IAudioCaptureSource
    {
        private readonly SyntheticSource inner = new(source, id);
        private double start;
        public AudioTrack Track => inner.Track;
        public void Start() { inner.Start(); start = AudioClock.Now; }
        public void Drain(Action<CapturedAudio> received)
        { if (source == AudioSource.System && AudioClock.Now - start > .15) throw new InvalidOperationException("Synthetic device unplugged"); inner.Drain(received); }
        public void Dispose() => inner.Dispose();
    }
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var recording = main.OpenRecording();
        while (main.Panels.Count < 4) main.AddReader();
        // Finish the four freshly-created layouts before measuring recording/render concurrency.
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await main.ToggleTimed(); var cursor = main.Playback.Cursor;
        var frames = 0; void Render(object? sender, EventArgs args) => frames++;
        System.Windows.Media.CompositionTarget.Rendering += Render;
        await recording.StartRecording(); await Task.Delay(1300);
        System.Windows.Media.CompositionTarget.Rendering -= Render;
        File.WriteAllText(Path.Combine(directory, "audio-reader-timing.json"), JsonSerializer.Serialize(new { frames, before = cursor, after = main.Playback.Cursor, main.Playback.Playing, main.Playback.WordsPerMinute, main.Playback.TimedMode, main.Voice.Running }));
        check(main.Recorder.State == RecordingState.Recording && SyntheticSource.OpenCount == 2, "Start opens two distinct synthetic capture sources through the production recorder");
        check(main.Recorder.Meter(AudioSource.Microphone).Level > 0 && main.Recorder.Meter(AudioSource.System).Level > 0, "Microphone and computer audio have independent live meters");
        check(main.Playback.Cursor > cursor + 1 && !main.Voice.Running, "Four-panel reader keeps advancing while local recording performs disk I/O; Deepgram remains off");
        capture(recording, Path.Combine(directory, "recording-window.png"));
        await main.Recorder.PauseAsync();
        check(main.Recorder.State == RecordingState.Paused && SyntheticSource.OpenCount == 0, "Pause releases both audio devices");
        await Task.Delay(250); await main.Recorder.ResumeAsync(); await Task.Delay(350);
        check(main.Recorder.State == RecordingState.Recording && SyntheticSource.OpenCount == 2, "Resume reopens the selected devices on the same session clock");
        await main.Recorder.StopAsync(); var path = main.Recorder.LastDirectory!;
        check(main.Recorder.State == RecordingState.Completed && SyntheticSource.OpenCount == 0, "Stop drains storage, releases devices and finalizes both tracks");
        var manifest = RecordingSession.ReadManifest(path);
        using var mic = new WaveFileReader(Path.Combine(path, "microphone.wav")); using var system = new WaveFileReader(Path.Combine(path, "system.wav"));
        check(Math.Abs(mic.TotalTime.TotalSeconds - system.TotalTime.TotalSeconds) < .001 && mic.TotalTime.TotalSeconds >= 1.8, "Independent WAV readers play both native-rate exports with matching timeline duration");
        check(manifest.Events.Any(e => e.Kind == "Paused") && manifest.Events.Any(e => e.Kind == "Resume") && manifest.Tracks.Select(t => t.Source).Distinct().Count() == 2, "Manifest labels sources and retains pause/resume markers");
        await recording.StartRecording(); await Task.Delay(150); recording.Close();
        for (var i = 0; i < 30 && main.Recorder.HasSession; i++) await Task.Delay(100);
        check(!main.Recorder.HasSession && SyntheticSource.OpenCount == 0, "Closing the recording window stops and saves without orphan capture devices");
        var failed = new RecordingService(Path.Combine(directory, "device-failure"), new DiagnosticLog(directory), (source, id) => source == AudioSource.System ? throw new InvalidOperationException("Synthetic disconnected output") : CreateSource(source, id));
        await failed.StartAsync("test", "test"); await Task.Delay(100);
        check(failed.State == RecordingState.Failed && failed.Message.Contains("System", StringComparison.Ordinal) && SyntheticSource.OpenCount == 0, "Disconnected output is reported explicitly and the other source is released");
        var blocked = Path.Combine(directory, "blocked-recording-root"); File.WriteAllText(blocked, "occupied");
        var disk = new RecordingService(blocked, new DiagnosticLog(directory), CreateSource);
        await disk.StartAsync("test", "test"); await Task.Delay(100);
        check(disk.State == RecordingState.Failed && disk.Message.Contains("writable", StringComparison.Ordinal) && SyntheticSource.OpenCount == 0, "Unwritable recording folder is distinguished from silence and device failure");
        var unplugged = new RecordingService(Path.Combine(directory, "unplugged"), new DiagnosticLog(directory), (source, id) => new DisconnectingSource(source, id));
        await unplugged.StartAsync("test", "test");
        for (var i = 0; i < 40 && unplugged.State != RecordingState.Failed; i++) await Task.Delay(100);
        check(unplugged.State == RecordingState.Failed && unplugged.Message.Contains("System", StringComparison.Ordinal) && SyntheticSource.OpenCount == 0,
            "Mid-recording output disconnection releases both devices and preserves a failed session");
        var failingDisk = new RecordingService(Path.Combine(directory, "mid-disk-failure"), new DiagnosticLog(directory), CreateSource);
        await failingDisk.StartAsync("test", "test");
        var blocker = Path.Combine(failingDisk.LastDirectory!, "session.json.tmp"); Directory.CreateDirectory(blocker);
        for (var i = 0; i < 40 && failingDisk.State != RecordingState.Failed; i++) await Task.Delay(100);
        check(failingDisk.State == RecordingState.Failed && failingDisk.Message.Contains("Disk-write", StringComparison.Ordinal) && SyntheticSource.OpenCount == 0,
            "Mid-recording disk-write failure is visible and stops both sources");
        Directory.Delete(blocker);
        var recovered = await Task.Run(() => RecordingSession.Recover(failingDisk.LastDirectory!));
        check(recovered.State == "Recovered" && recovered.Chunks.Count > 0, "Captured chunks remain recoverable after a manifest write failure");
        File.WriteAllText(Path.Combine(directory, "recording-summary.json"), JsonSerializer.Serialize(new { manifest.DurationSeconds, tracks = manifest.Tracks, events = manifest.Events, chunks = manifest.Chunks.Count, physicalDevicesOpened = false, deepgramUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
        main.Playback.Pause();
    }
}
