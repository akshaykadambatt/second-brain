using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record Microphone(string Id, string Name);
internal sealed record VoiceLatency(bool Final, double SubmittedSeconds, double TranscriptSeconds, double? ApproximateInterimLagMs, double DispatchMs, double MatchMs, bool Advanced);

internal sealed class VoiceService(Dispatcher dispatcher, ReaderSession session, ReaderPlayback playback, ApiKeyStore keys, DiagnosticLog log)
{
    private sealed class Run : IDisposable
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly ClientWebSocket Socket = new();
        public readonly Channel<(byte[] Data, WebSocketMessageType Type)> Outgoing = Channel.CreateBounded<(byte[], WebSocketMessageType)>(40);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker = Task.CompletedTask;
        public long LastAudio, LastSpeech, LastResult, Bytes;
        public int Peak, Results;
        public int SampleRate;
        public readonly System.Collections.Concurrent.ConcurrentQueue<VoiceLatency> Latencies = new();
        public string? Failure;
        public void Cancel() { Cancellation.Cancel(); Socket.Abort(); }
        public void Dispose() { Socket.Dispose(); Cancellation.Dispose(); }
    }
    private Run? active;
    private int generation;
    private Task pendingStop = Task.CompletedTask;
    public bool Running { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? Heard;
    public event Action<int>? Level;
    public event Action? Stopped;

    public static IReadOnlyList<Microphone> Microphones()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); defaultId = device.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        var result = new List<Microphone>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName + (device.ID == defaultId ? " (Windows communications default)" : "")));
        }
        return result.OrderByDescending(m => m.Id == defaultId).ToArray();
    }

    public void Reanchor()
    {
        if (!Running) return;
        playback.Voice.Reanchor();
        Status?.Invoke("Position changed. Pause briefly, then read from the selected word.");
    }

    public async Task StartAsync(string? deviceId = null, string? waveFile = null)
    {
        var stopping = StopAsync();
        var token = ++generation;
        await stopping;
        if (token != generation) return;
        Heard?.Invoke("Waiting for speech…");
        string key;
        try { key = keys.Load(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status?.Invoke(ex.Message); Stopped?.Invoke(); return; }
        if (deviceId is null && waveFile is null) { Status?.Invoke("Select a microphone first."); Stopped?.Invoke(); return; }
        var run = new Run(); active = run;
        Status?.Invoke("Connecting to Deepgram Nova-3…");
        run.Worker = Task.Run(() => RunAsync(run, token, key, deviceId, waveFile));
        await run.Started.Task;
    }

    private void Dispatch(int token, Action action) => dispatcher.BeginInvoke(() => { if (token == generation) action(); });

    private async Task RunAsync(Run run, int token, string key, string? deviceId, string? waveFile)
    {
        WasapiCapture? capture = null;
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WaveFileReader? wave = null;
        Task[] tasks = [];
        var ct = run.Cancellation.Token;
        try
        {
            WaveFormat format;
            if (waveFile is not null) { wave = new WaveFileReader(waveFile); format = wave.WaveFormat; }
            else
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(deviceId!);
                if (device.State != DeviceState.Active) throw new InvalidOperationException("Selected microphone is disconnected. Refresh the microphone list.");
                capture = new WasapiCapture(device, false, 50);
                format = capture.WaveFormat;
            }
            var floating = format.Encoding == WaveFormatEncoding.IeeeFloat
                || format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible))
                throw new InvalidOperationException("Unsupported microphone format. Choose another input device.");
            if (format is WaveFormatExtensible extensible && !floating && extensible.SubFormat != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                throw new InvalidOperationException("Unsupported microphone format. Choose another input device.");
            PcmAudio.ToMono16([], format.BitsPerSample, format.Channels, floating, out _);
            run.SampleRate = format.SampleRate;
            run.Socket.Options.SetRequestHeader("Authorization", "Token " + key);
            var uri = new Uri($"wss://api.deepgram.com/v1/listen?model=nova-3&language=en&encoding=linear16&sample_rate={format.SampleRate}&channels=1&interim_results=true&endpointing=300&punctuate=true");
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            { connect.CancelAfter(TimeSpan.FromSeconds(12)); await run.Socket.ConnectAsync(uri, connect.Token); }
            var now = Stopwatch.GetTimestamp(); run.LastAudio = run.LastSpeech = run.LastResult = now;
            log.Write($"Voice connected; provider=Deepgram; model=nova-3; input={(wave is null ? device!.FriendlyName : "synthetic WAV")}; sourceRate={format.SampleRate}; sourceBits={format.BitsPerSample}; channels={format.Channels}; float={floating}");
            void Audio(byte[] buffer, int count)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var pcm = PcmAudio.ToMono16(buffer.AsSpan(0, count), format.BitsPerSample, format.Channels, floating, out var level);
                    Interlocked.Exchange(ref run.LastAudio, Stopwatch.GetTimestamp());
                    if (level > 35) Interlocked.Exchange(ref run.LastSpeech, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref run.Peak, level);
                    if (!run.Outgoing.Writer.TryWrite((pcm, WebSocketMessageType.Binary)))
                    { run.Failure = "Connection cannot keep up with the microphone. Listening stopped; check your network and retry."; run.Cancel(); }
                }
                catch (Exception) { run.Failure = "Microphone audio could not be converted. Choose another input device."; run.Cancel(); }
            }
            if (capture is not null)
            {
                capture.DataAvailable += (_, e) => Audio(e.Buffer, e.BytesRecorded);
                capture.RecordingStopped += (_, _) =>
                {
                    if (!ct.IsCancellationRequested) { run.Failure = "Microphone disconnected or stopped. Refresh inputs and start again."; run.Cancel(); }
                };
            }
            tasks = [SendAsync(run), ReceiveAsync(run, token), WatchAsync(run, token)];
            capture?.StartRecording();
            if (wave is not null) tasks = [.. tasks, FeedWaveAsync(wave, Audio, run)];
            await dispatcher.InvokeAsync(() =>
            {
                if (token != generation) return;
                Running = true;
                playback.StartVoice();
                Status?.Invoke("Listening · Deepgram Nova-3 · microphone audio is streamed to Deepgram");
            });
            run.Started.TrySetResult();
            await await Task.WhenAny(tasks);
            if (!ct.IsCancellationRequested) run.Failure ??= "Deepgram closed the connection. Start listening again.";
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { run.Failure = "Deepgram connection timed out. Check your internet connection and retry."; }
        catch (WebSocketException ex)
        {
            log.Write("Voice connection failure; WebSocketError=" + ex.WebSocketErrorCode);
            run.Failure = ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal)
                ? "Deepgram rejected the API key. Check the key's permissions and account access."
                : "Deepgram connection failed. Check your internet connection, API key and account balance, then retry.";
        }
        catch (InvalidOperationException ex) { run.Failure = ex.Message; }
        catch (System.Text.Json.JsonException) { run.Failure = "Deepgram returned an unreadable response. Please reconnect."; }
        catch (Exception ex) { log.Write("Voice failure type=" + ex.GetType().Name); run.Failure = "Could not start or read the selected microphone. Check Windows microphone permissions and choose an available input."; }
        finally
        {
            run.Cancel();
            try { capture?.StopRecording(); } catch (Exception ex) { log.Write("Microphone stop failure type=" + ex.GetType().Name); }
            try { await Task.WhenAll(tasks); } catch (Exception) { }
            try { capture?.Dispose(); wave?.Dispose(); device?.Dispose(); enumerator?.Dispose(); }
            catch (Exception ex) { log.Write("Voice cleanup failure type=" + ex.GetType().Name); }
            log.Write($"Voice stopped; bytesSent={run.Bytes}; results={run.Results}; reason={run.Failure ?? "user stop"}");
            if (!run.Latencies.IsEmpty)
            {
                try
                {
                    var samples = await dispatcher.InvokeAsync(() => run.Latencies.ToArray());
                    var path = Path.Combine(Path.GetDirectoryName(keys.FilePath)!, "voice-latency.json");
                    File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new
                    {
                        note = "Approximate interim lag = submitted PCM duration minus provider start+duration. Includes buffering/network/recognition, not precise acoustic or server-only latency. Finals excluded from lag. Dispatch/matching are measured separately. No speech text stored.",
                        samples
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    File.Move(path + ".tmp", path, true);
                }
                catch (Exception ex) { log.Write("Voice latency report unavailable type=" + ex.GetType().Name); }
            }
            Dispatch(token, () => { Running = false; playback.StopVoice(); Level?.Invoke(0); Status?.Invoke(run.Failure ?? "Listening stopped."); Stopped?.Invoke(); });
            run.Started.TrySetResult();
        }
    }

    private static async Task SendAsync(Run run)
    {
        await foreach (var frame in run.Outgoing.Reader.ReadAllAsync(run.Cancellation.Token))
        {
            await run.Socket.SendAsync(frame.Data.AsMemory(), frame.Type, true, run.Cancellation.Token);
            if (frame.Type == WebSocketMessageType.Binary) Interlocked.Add(ref run.Bytes, frame.Data.Length);
        }
    }

    private async Task ReceiveAsync(Run run, int token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!run.Cancellation.IsCancellationRequested)
        {
            var result = await run.Socket.ReceiveAsync(buffer.AsMemory(), run.Cancellation.Token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 512_000) throw new InvalidOperationException("Deepgram sent an unexpectedly large response. Please reconnect.");
            if (!result.EndOfMessage) continue;
            var segment = DeepgramProtocol.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
            if (segment is null) continue;
            var receivedAt = Stopwatch.GetTimestamp();
            var submittedSeconds = Interlocked.Read(ref run.Bytes) / (2d * run.SampleRate);
            if (!string.IsNullOrWhiteSpace(segment.Text))
            { Interlocked.Exchange(ref run.LastResult, Stopwatch.GetTimestamp()); Interlocked.Increment(ref run.Results); }
            Dispatch(token, () =>
            {
                if (!string.IsNullOrWhiteSpace(segment.Text)) Heard?.Invoke(segment.Text);
                var moved = playback.Voice.Observe(segment, AudioSource.Microphone);
                run.Latencies.Enqueue(new(segment.Final, submittedSeconds, segment.Start + segment.Duration,
                    segment.Final ? null : (submittedSeconds - segment.Start - segment.Duration) * 1000,
                    Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds - playback.Voice.LastMatchMilliseconds,
                    playback.Voice.LastMatchMilliseconds, moved));
                while (run.Latencies.Count > 128) run.Latencies.TryDequeue(out _);
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    Status?.Invoke(session.Position == session.Words.Count ? "Finished. Reset to read again." : moved ? "Following your voice…" : "Speech received · waiting for a script match");
            });
        }
    }

    private async Task WatchAsync(Run run, int token)
    {
        var ticks = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(run.Cancellation.Token))
        {
            Dispatch(token, () => Level?.Invoke(Volatile.Read(ref run.Peak)));
            if (++ticks % 30 == 0 && !run.Outgoing.Writer.TryWrite(("{\"type\":\"KeepAlive\"}"u8.ToArray(), WebSocketMessageType.Text)))
                throw new InvalidOperationException("Audio connection is too slow. Listening stopped; reconnect to retry.");
            if (ticks % 50 != 0) continue;
            var silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref run.LastSpeech)).TotalSeconds;
            var audioAge = Stopwatch.GetElapsedTime(Interlocked.Read(ref run.LastAudio)).TotalSeconds;
            var resultAge = Stopwatch.GetElapsedTime(Interlocked.Read(ref run.LastResult)).TotalSeconds;
            log.Write($"Voice health; bytesSent={Interlocked.Read(ref run.Bytes)}; results={Volatile.Read(ref run.Results)}; level={Volatile.Read(ref run.Peak)}; audioAge={audioAge:F1}s; resultAge={resultAge:F1}s");
            if (audioAge > 5) Dispatch(token, () => Status?.Invoke("No audio frames from this microphone. Check permissions or select another input."));
            else if (silent > 5) Dispatch(token, () => Status?.Invoke("Microphone is quiet. Speak and check the meter; if it stays low, select another input."));
            else if (resultAge > 10) Dispatch(token, () => Status?.Invoke("Audio is being sent, but Deepgram has returned no recent words. Check the selected microphone and connection."));
        }
    }

    private static async Task FeedWaveAsync(WaveFileReader wave, Action<byte[], int> audio, Run run)
    {
        var buffer = new byte[wave.WaveFormat.AverageBytesPerSecond / 20 / wave.WaveFormat.BlockAlign * wave.WaveFormat.BlockAlign];
        int read;
        while ((read = wave.Read(buffer, 0, buffer.Length)) > 0)
        { audio(buffer, read); await Task.Delay(50, run.Cancellation.Token); }
        run.Outgoing.Writer.TryWrite(("{\"type\":\"Finalize\"}"u8.ToArray(), WebSocketMessageType.Text));
        await Task.Delay(5000, run.Cancellation.Token);
        run.Cancel();
    }

    public Task StopAsync()
    {
        generation++; Running = false;
        playback.StopVoice();
        var old = active; active = null;
        if (old is not null) { old.Cancel(); pendingStop = FinishAsync(old); }
        Level?.Invoke(0);
        return pendingStop;
    }
    private static async Task FinishAsync(Run run) { try { await run.Worker; } finally { run.Dispose(); } }
}
