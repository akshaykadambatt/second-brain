using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Speech.Synthesis;
using System.Speech.AudioFormat;
using NAudio.Wave;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class TranscriptionTests
{
    internal sealed class WaveSource(AudioSource source, string path) : IAudioCaptureSource
    {
        private readonly WaveFileReader wave = new(path);
        private double next, start;
        public AudioTrack Track { get; } = new(source, "generated-wave", "Generated speech (no hardware)", 16000);
        public void Start() { start = next = AudioClock.Now; }
        public void Drain(Action<CapturedAudio> received)
        {
            while (next + .02 <= AudioClock.Now)
            {
                var bytes = new byte[640];
                if (next - start > 2)
                {
                    var offset = 0;
                    while (offset < bytes.Length)
                    { var count = wave.Read(bytes, offset, bytes.Length - offset); if (count == 0) break; offset += count; }
                }
                received(new(next, bytes, 50, false, false, false)); next += .02;
            }
        }
        public void Dispose() => wave.Dispose();
    }
    public static async Task RunLive(MainWindow main, string directory, Action<bool, string> check)
    {
        var mic = Path.Combine(directory, "microphone-fixture.wav"); var system = Path.Combine(directory, "system-fixture.wav");
        using (var voice = new SpeechSynthesizer())
        {
            voice.SetOutputToWaveFile(mic, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            voice.Speak("The microphone says the blue bicycle is ready. This is the microphone test.");
            voice.SetOutputToNull();
            voice.SetOutputToWaveFile(system, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            voice.Speak("The computer says the yellow submarine is ready. This is the computer audio test.");
            voice.SetOutputToNull();
        }
        var transcriber = new RecordingTranscriber(new ApiKeyStore(directory).Load, new DiagnosticLog(directory)) { Enabled = true };
        var recorder = new RecordingService(Path.Combine(directory, "recordings"), new DiagnosticLog(directory),
            (source, _) => new WaveSource(source, source == AudioSource.Microphone ? mic : system), transcriber);
        try
        {
            await recorder.StartAsync("generated", "generated"); await Task.Delay(14000); await recorder.StopAsync();
            var entries = TranscriptLog.Read(recorder.LastDirectory!); var finals = entries.Where(e => e.Kind == "Final").ToArray();
            check(recorder.State == RecordingState.Completed && !transcriber.View.Status.Contains("unavailable", StringComparison.Ordinal), "Real Deepgram dual-stream session closes and saves cleanly with generated WAVs only");
            check(finals.Any(e => e.Source == AudioSource.Microphone && e.Text.Contains("bicycle", StringComparison.OrdinalIgnoreCase)), "Real microphone-labeled stream recognizes its distinctive generated phrase");
            check(finals.Any(e => e.Source == AudioSource.System && e.Text.Contains("submarine", StringComparison.OrdinalIgnoreCase)), "Real system-labeled stream recognizes its different generated phrase");
            check(finals.All(e => e.Start >= 0 && e.End <= 14.5 && !string.IsNullOrWhiteSpace(e.Id)), "Real provider segments retain bounded session timestamps and identities");
            File.WriteAllText(Path.Combine(directory, "live-transcription-summary.json"), JsonSerializer.Serialize(new { entries, physicalDevicesOpened = false, providerNetworkUsed = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await recorder.StopAsync(); }
    }
    private static int systemConnections;
    internal static ITranscriptConnection CreateConnection(AudioSource source) => new FakeConnection(source,
        source == AudioSource.System && Interlocked.Increment(ref systemConnections) == 1);
    private sealed class FakeConnection(AudioSource source, bool interrupt, int closeDelay = 0) : ITranscriptConnection
    {
        private readonly Channel<SpeechSegment> results = Channel.CreateUnbounded<SpeechSegment>();
        private int rate;
        private double seconds, finalized;
        public Task Connect(int sampleRate, string key, CancellationToken ct) { rate = sampleRate; return Task.CompletedTask; }
        public Task Send(byte[] pcm, CancellationToken ct)
        {
            seconds += pcm.Length / (2d * rate);
            if (interrupt && seconds > .85) { results.Writer.TryComplete(new IOException("Injected network interruption")); throw new IOException("Injected network interruption"); }
            if (seconds - finalized >= .3)
            {
                var end = seconds;
                results.Writer.TryWrite(new(finalized, end - finalized, "incorrect provisional draft", false, false, .5f));
                var final = new SpeechSegment(finalized, end - finalized, source + " finalized sentence.", true, true, .98f);
                results.Writer.TryWrite(final); results.Writer.TryWrite(final);
                results.Writer.TryWrite(final with { Text = "stale provisional draft", Final = false });
                finalized = end;
            }
            return Task.CompletedTask;
        }
        public async Task Control(string type, CancellationToken ct)
        {
            if (type == "CloseStream")
            {
                if (closeDelay > 0) await Task.Delay(closeDelay, ct);
                if (seconds > finalized) results.Writer.TryWrite(new(finalized, seconds - finalized, source + " final tail.", true, true, .98f));
                results.Writer.TryComplete();
            }
        }
        public async Task<SpeechSegment?> Receive(CancellationToken ct) => await results.Reader.WaitToReadAsync(ct) ? await results.Reader.ReadAsync(ct) : null;
        public void Dispose() => results.Writer.TryComplete();
    }
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        systemConnections = 0;
        var keys = new ApiKeyStore(directory); var import = Path.Combine(directory, "synthetic-key.txt");
        File.WriteAllText(import, "synthetic-test-key-not-a-real-provider-key"); keys.Import(import); File.Delete(import);
        var window = main.OpenRecording(); window.TranscribeCheck.IsChecked = true;
        while (main.Panels.Count < 4) main.AddReader();
        main.Playback.Pause(); var position = main.Session.Position;
        await window.StartRecording(); await Task.Delay(3100);
        check(main.Recorder.State == RecordingState.Recording && systemConnections >= 2, "System disconnection reconnects independently while both local tracks keep recording");
        check(main.Session.Position == position && !main.Voice.Running, "Neither context transcript stream can move the reader or start voice capture");
        var cursor = main.Playback.Cursor; await main.ToggleTimed(); await Task.Delay(400);
        check(main.Playback.Cursor > cursor, "Four-panel timed reader remains responsive during dual transcription");
        capture(window, Path.Combine(directory, "transcription-window.png"));
        await main.Recorder.PauseAsync();
        check(AudioRecordingTests.SyntheticSource.OpenCount == 0, "Pause flushes transcripts and releases capture devices");
        await Task.Delay(150); await main.Recorder.ResumeAsync(); await Task.Delay(450); await main.Recorder.StopAsync();
        var folder = main.Recorder.LastDirectory!; var manifest = RecordingSession.ReadManifest(folder); var entries = TranscriptLog.Read(folder);
        var finals = entries.Where(e => e.Kind == "Final").ToArray();
        check(finals.Length >= 8 && finals.Select(e => e.Source).Distinct().Count() == 2, "Both labeled sources save final segments through the production recording/transcription path");
        check(finals.Select(e => e.Id).Distinct().Count() == finals.Length && finals.All(e => !e.Text.Contains("draft", StringComparison.Ordinal)), "Duplicate finals and revised/stale drafts do not pollute durable transcript history");
        check(entries.Any(e => e.Kind == "Gap" && e.Source == AudioSource.System && e.End > e.Start) && entries.Any(e => e.Kind == "Gap" && e.Source is null), "Network gaps and recording pauses are explicit timestamped records");
        check(finals.All(e => e.SessionId == manifest.Id && e.Start >= 0 && e.End <= manifest.DurationSeconds + .05 && e.End >= e.Start), "Final segments link to the session and its audio clock across reconnect and pause/resume");
        check(entries.Count(e => e.Kind == "RunStart") == 2 && entries.Count(e => e.Kind == "RunEnd") == 2 && File.ReadAllText(Path.Combine(folder, "transcript.md")).Contains("system.wav#t=", StringComparison.Ordinal), "Each run is closed and Markdown includes source audio references");
        check(main.Recorder.State == RecordingState.Completed && AudioRecordingTests.SyntheticSource.OpenCount == 0 && !main.Transcriber.View.Status.Contains("unavailable", StringComparison.Ordinal), "Stop saves audio and transcripts and leaves no capture source running");
        window.Close(); main.Playback.Pause();

        var bad = new RecordingTranscriber(() => throw new InvalidOperationException("missing test key"), new DiagnosticLog(directory), CreateConnection) { Enabled = true };
        var recorder = new RecordingService(Path.Combine(directory, "missing-key"), new DiagnosticLog(directory), AudioRecordingTests.CreateSource, bad);
        await recorder.StartAsync("test", "test"); await Task.Delay(100);
        check(recorder.State == RecordingState.Recording && bad.View.Status.Contains("unavailable", StringComparison.Ordinal), "Missing protected key is visible without stopping local recording");
        await recorder.StopAsync();
        check(TranscriptLog.Read(recorder.LastDirectory!).Any(e => e.Kind == "Gap" && e.End > e.Start), "Unavailable credentials leave a durable gap for the attempted transcription run");
        var saveFault = new RecordingTranscriber(() => "synthetic", new DiagnosticLog(directory), CreateConnection) { Enabled = true };
        var saveRecorder = new RecordingService(Path.Combine(directory, "export-failure"), new DiagnosticLog(directory), AudioRecordingTests.CreateSource, saveFault);
        await saveRecorder.StartAsync("test", "test"); await Task.Delay(400);
        Directory.CreateDirectory(Path.Combine(saveRecorder.LastDirectory!, "transcript.md.tmp"));
        await saveRecorder.StopAsync();
        check(saveRecorder.State == RecordingState.Completed && saveFault.View.Status.Contains("unable to save", StringComparison.Ordinal) && TranscriptLog.Read(saveRecorder.LastDirectory!).Any(e => e.Kind == "Final"), "Transcript export failure is visible, retains durable JSON lines and does not fail the audio recording");
        var slow = new RecordingTranscriber(() => "synthetic", new DiagnosticLog(directory), source => new FakeConnection(source, false, 1000)) { Enabled = true };
        var slowRecorder = new RecordingService(Path.Combine(directory, "slow-finalization"), new DiagnosticLog(directory), AudioRecordingTests.CreateSource, slow);
        await slowRecorder.StartAsync("test", "test"); await Task.Delay(350); var beforeStop = slowRecorder.Elapsed;
        await slowRecorder.StopAsync();
        check(RecordingSession.ReadManifest(slowRecorder.LastDirectory!).DurationSeconds < beforeStop + .2 && AudioRecordingTests.SyntheticSource.OpenCount == 0,
            "Waiting for cloud finalization does not extend recorded audio or retain devices");
        File.WriteAllText(Path.Combine(directory, "transcription-summary.json"), JsonSerializer.Serialize(new { finals = finals.Length, entries, physicalDevicesOpened = false, providerNetworkUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
        File.Delete(keys.FilePath);
    }
}
