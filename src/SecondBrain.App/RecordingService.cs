using System.IO;
using System.Threading.Channels;
using SecondBrain.Core;

namespace SecondBrain.App;

internal enum RecordingState { Idle, Starting, Recording, Paused, Stopping, Completed, Failed }
internal sealed class RecordingService(string root, DiagnosticLog log, Func<AudioSource, string, IAudioCaptureSource>? factory = null, RecordingTranscriber? transcriber = null)
{
    private sealed class CaptureRun
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Channel<AudioPacket> Queue = Channel.CreateBounded<AudioPacket>(512);
        public Task Worker = Task.CompletedTask;
        public string? Error, Code;
    }
    private readonly SemaphoreSlim controls = new(1);
    private readonly Func<AudioSource, string, IAudioCaptureSource> create = factory ?? ((source, id) => new AudioCaptureSource(source, id));
    private CaptureRun? active;
    private RecordingSession? session;
    private string microphone = "", output = "";
    private double origin, stoppedAt;
    internal double ClockOrigin => origin;
    private readonly int[] levels = new int[2];
    private readonly long[] lastPackets = new long[2];
    public RecordingState State { get; private set; }
    public string Message { get; private set; } = "Ready · recording saves audio locally";
    public string? LastDirectory { get; private set; }
    public bool HasSession => session is not null || active is not null;
    public bool Busy => State is RecordingState.Starting or RecordingState.Stopping;
    public double Elapsed => State is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping ? Math.Max(0, AudioClock.Now - origin) : stoppedAt;
    public event Action? Changed;
    private void SetState(RecordingState state, string message)
    { State = state; Message = message; log.Write("Recording state=" + state + "; " + message); Changed?.Invoke(); }
    public (int Level, string Status) Meter(AudioSource source)
    {
        if (State != RecordingState.Recording) return (0, State == RecordingState.Paused ? "Paused · devices released" : "Not recording");
        var stamp = Interlocked.Read(ref lastPackets[(int)source]);
        var age = stamp == 0 ? double.PositiveInfinity : System.Diagnostics.Stopwatch.GetElapsedTime(stamp).TotalSeconds;
        if (age > 1) return (0, source == AudioSource.System ? "Output idle · no audio packets" : "No microphone packets");
        var level = Volatile.Read(ref levels[(int)source]);
        return (level, level > 5 ? "Audio arriving" : "Quiet · capture is active");
    }
    public async Task StartAsync(string microphoneId, string outputId)
    {
        await controls.WaitAsync();
        try
        {
            if (HasSession || Busy) return;
            microphone = microphoneId; output = outputId; stoppedAt = 0; origin = AudioClock.Now; LastDirectory = null;
            await StartRun();
        }
        finally { controls.Release(); }
    }
    private async Task StartRun()
    {
        SetState(RecordingState.Starting, "Opening microphone and computer audio…");
        Array.Clear(levels); Array.Clear(lastPackets);
        var run = new CaptureRun(); active = run;
        run.Worker = Task.Factory.StartNew(() => CaptureLoop(run), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var started = await run.Started.Task;
        if (started) SetState(RecordingState.Recording, "Recording locally · microphone and computer audio");
        else SetState(RecordingState.Failed, run.Error ?? "Could not start recording.");
        _ = Observe(run);
    }
    private async Task Observe(CaptureRun run)
    {
        await run.Worker;
        if (active != run || run.Error is null) return;
        stoppedAt = Math.Max(0, AudioClock.Now - origin);
        active = null; run.Cancellation.Dispose();
        SetState(RecordingState.Failed, run.Error + " Earlier chunks remain available for recovery.");
    }
    private void CaptureLoop(CaptureRun run)
    {
        var sources = new List<IAudioCaptureSource>(); Task writer = Task.CompletedTask;
        try
        {
            foreach (var (source, id) in new[] { (AudioSource.Microphone, microphone), (AudioSource.System, output) })
            {
                try { sources.Add(create(source, id)); }
                catch (Exception ex) { throw new InvalidOperationException($"{source} device could not open ({ex.GetType().Name}). Refresh devices and check Windows audio permissions.", ex); }
            }
            if (session is null)
            {
                var startedUtc = DateTimeOffset.UtcNow; origin = AudioClock.Now;
                try { session = new(root, sources.Select(s => s.Track).ToArray(), startedUtc); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { run.Code = "DiskWriteFailure"; throw new IOException("Recording folder is not writable. Choose a writable data folder or free disk space.", ex); }
                LastDirectory = session.DirectoryPath;
                log.Write("Recording session=" + session.Manifest.Id + "; " + string.Join(", ", sources.Select(s => $"{s.Track.Source}={s.Track.SampleRate}Hz")));
            }
            else
            {
                if (sources.Any(s => session.Manifest.Tracks.Single(t => t.Source == s.Track.Source).SampleRate != s.Track.SampleRate))
                    throw new InvalidOperationException("A device changed audio format. Stop this session and start a new recording.");
                session.Mark("Recording", AudioClock.Now - origin, "Resume");
            }
            transcriber?.Begin(session.DirectoryPath, session.Manifest, AudioClock.Now - origin);
            writer = Task.Run(async () =>
            {
                try { await foreach (var packet in run.Queue.Reader.ReadAllAsync()) { session.Write(packet); transcriber?.Offer(packet); } }
                catch (Exception ex)
                {
                    run.Code = ex is not InvalidDataException && ex is (IOException or UnauthorizedAccessException) ? "DiskWriteFailure" : "RecordingDataFailure";
                    run.Error = run.Code == "DiskWriteFailure" ? "Disk-write failure. Recording stopped; check free space and folder access." : "Invalid audio data. Recording stopped.";
                    log.Write("Recording writer failure type=" + ex.GetType().Name); run.Cancellation.Cancel();
                }
            });
            foreach (var source in sources) source.Start();
            run.Started.TrySetResult(true);
            var began = AudioClock.Now;
            while (!run.Cancellation.IsCancellationRequested)
            {
                foreach (var source in sources)
                {
                    try
                    {
                        source.Drain(packet =>
                        {
                            if (run.Cancellation.IsCancellationRequested) return;
                            Volatile.Write(ref levels[(int)source.Track.Source], packet.Level);
                            Interlocked.Exchange(ref lastPackets[(int)source.Track.Source], System.Diagnostics.Stopwatch.GetTimestamp());
                            if (!run.Queue.Writer.TryWrite(new(source.Track.Source, packet.ClockSeconds - origin, packet.Pcm, packet.Silent, packet.Discontinuity, packet.EstimatedTime)))
                                throw new InvalidOperationException("Recording storage cannot keep up; audio queue is full.");
                        });
                    }
                    catch (Exception ex) { run.Code = "CaptureFailure"; throw new InvalidOperationException($"{source.Track.Source} capture failed: {ex.Message}", ex); }
                }
                var micLast = Interlocked.Read(ref lastPackets[0]);
                if (AudioClock.Now - began > 5 && (micLast == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(micLast).TotalSeconds > 5))
                    throw new InvalidOperationException("Microphone capture stalled: no packets for five seconds.");
                if (AudioClock.Now - origin > Math.Min(12 * 3600, (uint.MaxValue - 65536d) / (2 * sources.Max(s => s.Track.SampleRate))))
                    throw new InvalidOperationException("The session or WAV size limit was reached. Stop and recover this session, then start a new one.");
                run.Cancellation.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception ex)
        { run.Error ??= ex.Message; run.Code ??= "CaptureFailure"; log.Write("Recording failure type=" + ex.GetType().Name); }
        finally
        {
            var captureEnded = Math.Max(0, AudioClock.Now - origin);
            foreach (var source in sources)
                try { source.Dispose(); } catch (Exception ex) { log.Write("Audio device release failure type=" + ex.GetType().Name); run.Error ??= "Audio device release failed. Restart the app before recording again."; }
            run.Queue.Writer.TryComplete(); writer.GetAwaiter().GetResult();
            transcriber?.Finish(run.Error is null ? "Capture stopped or paused" : "Capture failed", captureEnded).GetAwaiter().GetResult();
            if (run.Error is not null && session is not null)
            {
                try { session.Mark("Failed", Math.Max(0, AudioClock.Now - origin), run.Code ?? "CaptureFailure"); }
                catch (Exception ex) { log.Write("Recording error manifest unavailable type=" + ex.GetType().Name); }
                session.Dispose(); session = null;
            }
            run.Started.TrySetResult(false);
        }
    }
    private async Task EndRun()
    {
        var run = active; active = null;
        if (run is null) return;
        run.Cancellation.Cancel(); await run.Worker; run.Cancellation.Dispose();
        if (run.Error is not null) throw new IOException(run.Error);
    }
    public async Task PauseAsync()
    {
        await controls.WaitAsync();
        try
        {
            if (State != RecordingState.Recording) return;
            var pausedAt = Math.Max(0, AudioClock.Now - origin);
            SetState(RecordingState.Stopping, "Pausing and releasing audio devices…");
            await EndRun();
            await Task.Run(() => session!.Mark("Paused", pausedAt));
            SetState(RecordingState.Paused, "Paused · neither source is being captured. Resume reopens the same devices.");
        }
        catch (Exception ex) { FailControl(ex); }
        finally { controls.Release(); }
    }
    public async Task ResumeAsync()
    {
        await controls.WaitAsync();
        try { if (State == RecordingState.Paused) await StartRun(); }
        finally { controls.Release(); }
    }
    public async Task StopAsync()
    {
        await controls.WaitAsync();
        try
        {
            if (!HasSession) return;
            stoppedAt = Math.Max(0, AudioClock.Now - origin);
            SetState(RecordingState.Stopping, "Stopping devices and assembling the two WAV tracks…");
            await EndRun();
            if (session is not null) await Task.Run(() => session.Complete(stoppedAt));
            session?.Dispose(); session = null;
            SetState(RecordingState.Completed, "Saved microphone.wav and system.wav with a session manifest.");
        }
        catch (Exception ex) { FailControl(ex); }
        finally { controls.Release(); }
    }
    private void FailControl(Exception ex)
    {
        stoppedAt = Math.Max(0, AudioClock.Now - origin);
        try { session?.Mark("Failed", stoppedAt, "StorageOrFinalizationFailure"); } catch (Exception) { }
        session?.Dispose(); session = null;
        log.Write("Recording control failure type=" + ex.GetType().Name);
        SetState(RecordingState.Failed, ex.Message + " Earlier chunks are available for recovery.");
    }
}
