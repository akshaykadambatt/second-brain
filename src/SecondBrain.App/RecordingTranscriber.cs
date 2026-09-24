using System.IO;
using System.Threading.Channels;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record TranscriptView(string Status, string Microphone, string System, string[] Recent);
internal sealed record LiveSpeech(Guid SessionId, AudioSource Source, Guid ConnectionId, SpeechSegment Segment, double ReceivedAt)
{
    // Provider word timing for diagnostics only; reader alignment keeps its existing segment mapping.
    public double? SpeechEndedSeconds { get; init; }
    public string? SpeakerLabel { get; init; }
}

// Capture only offers packets; networking and transcript storage cannot block it.
// This service has no ReaderSession reference: system speech cannot move a reader.
internal sealed class RecordingTranscriber(Func<string> loadKey, DiagnosticLog log,
    Func<AudioSource, ITranscriptConnection>? connectionFactory = null)
{
    private sealed class SourceRun(AudioTrack track)
    {
        public readonly AudioTrack Track = track;
        public readonly Channel<AudioPacket> Queue = Channel.CreateBounded<AudioPacket>(128);
        public Task Worker = Task.CompletedTask;
        public int Overflow;
        public double Through;
        public string Status = "Connecting…", Interim = "";
    }
    private readonly object gate = new();
    private readonly Queue<string> recent = new();
    private readonly Queue<TranscriptEntry> assistantEvents = new();
    private bool assistantOverflow;
    private readonly Queue<LiveSpeech> liveSpeech = new();
    public LiveSpeech[] DrainSpeech()
    { lock (gate) { var result = liveSpeech.ToArray(); liveSpeech.Clear(); return result; } }
    public TranscriptEntry[] DrainAssistantEvents(out bool dropped)
    {
        lock (gate) { var result = assistantEvents.ToArray(); assistantEvents.Clear(); dropped = assistantOverflow; assistantOverflow = false; return result; }
    }
    public SpeakerOptions SpeakerOptions { get; set; } = new();
    public string[] Vocabulary { get; set; } = [];
    private SpeakerOptions activeSpeakerOptions = new();
    private string[] activeVocabulary = [];
    private AudioSpeakers? speakerLabels;
    private string vocabularyStatus = "";
    private SourceRun[] sources = [];
    private CancellationTokenSource? cancellation;
    private TranscriptLog? journal;
    private TranscriptDetails? details;
    private readonly object detailsGate = new();
    private volatile string? detailFailure;
    private string? failure;
    private string idle = "Live transcription is off.";
    private double runStart, runEnd, previousEnd;
    private Guid lastSession;
    public bool Enabled { get; set; }
    public TranscriptView View
    {
        get { lock (gate) return new((failure ?? (sources.Length == 0 ? idle : string.Join(" · ", sources.Select(s => s.Track.Source + ": " + s.Status)))) + vocabularyStatus + (detailFailure is null ? "" : " · " + detailFailure),
            sources.FirstOrDefault(s => s.Track.Source == AudioSource.Microphone)?.Interim ?? "",
            sources.FirstOrDefault(s => s.Track.Source == AudioSource.System)?.Interim ?? "", recent.ToArray()); }
    }
    public void Begin(string directory, RecordingManifest manifest, double seconds)
    {
        if (!Enabled)
        {
            lock (gate) { idle = "Live transcription is off · this session saves audio only."; failure = null; recent.Clear(); }
            return;
        }
        try
        {
            lock (gate)
            {
                failure = null; runStart = Math.Max(0, seconds); runEnd = runStart;
                if (lastSession != manifest.Id)
                {
                    if (!SpeakerOptions.IsValid) throw new InvalidDataException("Invalid microphone identity.");
                    recent.Clear(); previousEnd = 0; lastSession = manifest.Id; activeSpeakerOptions = SpeakerOptions;
                    speakerLabels = new(manifest.Id, activeSpeakerOptions); activeVocabulary = StreamingSpeechOptions.Keyterms(Vocabulary);
                    vocabularyStatus = " · " + (activeSpeakerOptions.SeparateSpeakers ? "audio speaker labels on (provisional)" : "audio speaker labels off")
                        + (activeVocabulary.Length < Vocabulary.Length ? $" · vocabulary {activeVocabulary.Length}/{Vocabulary.Length} terms sent (provider budget)" : "");
                }
                journal = new(directory, manifest.Id); cancellation = new();
                detailFailure = null;
                try { details = new(directory, manifest.Id); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { detailFailure = "Word details unavailable; original transcript continues"; log.Write("Word-detail setup failure=" + ex.GetType().Name); }
                if (previousEnd > 0 && seconds > previousEnd) Record("Gap", null, previousEnd, seconds, "Recording paused; no transcription during this interval.");
                Record("RunStart", null, runStart, runStart, "Live Deepgram Nova-3 transcription enabled; each source connects separately.");
                var key = loadKey();
                sources = manifest.Tracks.Select(t => new SourceRun(t) { Through = runStart }).ToArray();
                foreach (var source in sources) source.Worker = Task.Run(() => Work(source, key, cancellation.Token));
            }
        }
        catch (Exception ex)
        {
            FailStorageOrSetup(ex);
            // Keep a writable journal until stop so a missing key cannot leave
            // this run absent from the saved session's completeness record.
            if (journal is null) { cancellation?.Dispose(); cancellation = null; }
            lock (gate) sources = [];
        }
    }
    public void Offer(AudioPacket packet)
    {
        foreach (var source in sources)
            if (source.Track.Source == packet.Source && !source.Queue.Writer.TryWrite(packet)) Interlocked.Exchange(ref source.Overflow, 1);
    }
    public async Task Finish(string reason, double seconds)
    {
        if (journal is null) return;
        runEnd = Math.Max(runStart, seconds);
        foreach (var source in sources) source.Queue.Writer.TryComplete();
        cancellation!.CancelAfter(TimeSpan.FromSeconds(7));
        await Task.WhenAll(sources.Select(s => s.Worker));
        try
        {
            if (failure is not null) Record("Gap", null, runStart, runEnd, "Transcription setup or storage failed; this run may be incomplete.");
            Record("RunEnd", null, runEnd, runEnd, reason);
            journal.ExportMarkdown();
            lock (gate) idle = "Transcript saved · " + reason + ". Gaps, if any, are listed in the log.";
        }
        catch (Exception ex) { FailStorageOrSetup(ex); }
        finally { previousEnd = runEnd; details?.Dispose(); details = null; journal.Dispose(); journal = null; cancellation.Dispose(); cancellation = null; lock (gate) sources = []; }
    }
    private void FailStorageOrSetup(Exception ex)
    {
        log.Write("Transcription setup/storage failure type=" + ex.GetType().Name);
        lock (gate) failure = "Transcription unavailable or unable to save. Local recording continues. Check the protected key, disk space and folder access; restart recording to retry.";
        cancellation?.Cancel();
    }
    private TranscriptEntry? Record(string kind, AudioSource? source, double start, double end, string text, Guid connection = default, string? id = null, string? attribution = null)
    {
        var entry = new TranscriptEntry(journal!.SessionId, kind, source, Math.Max(0, start), Math.Max(Math.Max(0, start), end), text, connection, id);
        try
        {
            if (!journal.Append(entry)) return null;
            lock (gate)
            {
                assistantEvents.Enqueue(entry);
                while (assistantEvents.Count > 256) { assistantEvents.Dequeue(); assistantOverflow = true; }
            }
            if (kind is "Final" or "Gap") lock (gate)
            {
                recent.Enqueue($"{TimeSpan.FromSeconds(entry.Start):hh\\:mm\\:ss} {source?.ToString() ?? "Session"}" + (attribution is null ? "" : " · " + attribution) + $" · {(kind == "Gap" ? "GAP: " : "")}{text}");
                while (recent.Count > 80) recent.Dequeue();
            }
            return entry;
        }
        catch (Exception ex) { FailStorageOrSetup(ex); throw; }
    }
    private async Task Work(SourceRun source, string key, CancellationToken ct)
    {
        var attempts = 0; var uncertain = runStart; var disconnect = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (source.Queue.Reader.Completion.IsCompleted) break;
                var epoch = Guid.NewGuid(); var map = new TranscriptTimeline(source.Track.SampleRate);
                var finalized = -1d; var finalThrough = uncertain; var sentThrough = uncertain;
                using var socket = connectionFactory?.Invoke(source.Track.Source) ?? new TranscriptConnection(source.Track.Source == AudioSource.System && activeSpeakerOptions.SeparateSpeakers, activeVocabulary);
                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task receiving = Task.CompletedTask;
                var first = true; var finishing = false;
                try
                {
                    lock (gate) { source.Status = attempts == 0 ? "Connecting…" : "Reconnecting · possible transcript gap"; source.Interim = ""; }
                    await socket.Connect(source.Track.SampleRate, key, ct);
                    // No backlog replay after disconnect: keep live latency bounded.
                    // Local WAV audio is retained and any omitted interval is explicit.
                    if (disconnect || Interlocked.Exchange(ref source.Overflow, 0) != 0)
                        while (source.Queue.Reader.TryRead(out _)) { }
                    lock (gate) source.Status = "Connected · waiting for audio";
                    receiving = Receive();
                    var lastSend = AudioClock.Now;
                    while (!ct.IsCancellationRequested)
                    {
                        if (receiving.IsCompleted) { await receiving; if (!finishing) throw new IOException("Stream closed before stop."); break; }
                        if (Interlocked.Exchange(ref source.Overflow, 0) != 0) throw new IOException("Transcription queue overflow.");
                        if (source.Queue.Reader.TryRead(out var packet))
                        {
                            var seconds = Math.Max(0, packet.Seconds);
                            if (first)
                            {
                                if (seconds > uncertain + .02 || disconnect) Record("Gap", source.Track.Source, uncertain, seconds, "Connection startup/recovery; words may be missing.", epoch);
                                first = false; attempts = 0;
                            }
                            else if (seconds > sentThrough + .05 || packet.Discontinuity)
                                Record("Gap", source.Track.Source, sentThrough, Math.Max(sentThrough, seconds), "No captured packets or a capture discontinuity; possible missing speech.", epoch);
                            map.Add(seconds, packet.Mono16.Length / 2);
                            await socket.Send(packet.Mono16, connectionStop.Token);
                            sentThrough = seconds + packet.Mono16.Length / (2d * source.Track.SampleRate);
                            source.Through = sentThrough; lastSend = AudioClock.Now;
                        }
                        else if (source.Queue.Reader.Completion.IsCompleted)
                        {
                            finishing = true;
                            lock (gate) source.Status = "Finalizing…";
                            await socket.Control("CloseStream", connectionStop.Token);
                            await receiving.WaitAsync(TimeSpan.FromSeconds(5), ct);
                            if (first && runEnd > uncertain) Record("Gap", source.Track.Source, uncertain, runEnd, "No audio reached transcription during this run.", epoch);
                            lock (gate) { source.Status = "Saved"; source.Interim = ""; }
                            return;
                        }
                        else
                        {
                            if (AudioClock.Now - lastSend > 3) { await socket.Control("KeepAlive", connectionStop.Token); lastSend = AudioClock.Now; }
                            await Task.Delay(15, connectionStop.Token);
                        }
                    }
                    async Task Receive()
                    {
                        while (await socket.Receive(connectionStop.Token) is { } segment)
                        {
                            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.Duration) || segment.Start < 0 || segment.Duration < 0 || segment.Text.Length > 32000)
                                throw new InvalidDataException("Invalid transcription segment.");
                            if (segment.Start < finalized - .001) continue;
                            var start = map.Map(segment.Start); var end = map.Map(segment.Start + segment.Duration, true);
                            var mappedWords = segment.Words.Select(w => w with { Start = map.Map(w.Start), End = map.Map(w.End, true) }).ToArray();
                            var segmentId = epoch.ToString("N") + ":" + ((long)Math.Round(segment.Start * source.Track.SampleRate)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            TranscriptDetail? labeled = null; string? attribution = null;
                            if (segment.Final && !string.IsNullOrWhiteSpace(segment.Text)) lock (detailsGate)
                            {
                                var original = new TranscriptEntry(journal!.SessionId, "Final", source.Track.Source, start, Math.Max(start, end), segment.Text, epoch, segmentId);
                                labeled = speakerLabels!.Label(TranscriptDetails.From(original, mappedWords, segment.WordTimingStatus));
                                attribution = string.Join(" / ", labeled.Words.Select(w => w.SpeakerLabel ?? "Unknown").Distinct());
                                if (attribution.Length == 0) attribution = "Unknown";
                            }
                            lock (gate)
                            {
                                liveSpeech.Enqueue(new(journal!.SessionId, source.Track.Source, epoch,
                                    segment with { Start = start, Duration = Math.Max(0, end - start), LastWordEnd = end, Words = mappedWords }, AudioClock.Now)
                                { SpeakerLabel = attribution, SpeechEndedSeconds = segment.LastWordEnd is { } wordEnd && double.IsFinite(wordEnd)
                                    && wordEnd >= segment.Start && wordEnd <= segment.Start + segment.Duration + .001 ? map.Map(wordEnd, true) : null });
                                while (liveSpeech.Count > 128) liveSpeech.Dequeue();
                            }
                            if (segment.Final)
                            {
                                finalized = Math.Max(finalized, segment.Start + Math.Max(.001, segment.Duration)); finalThrough = Math.Max(finalThrough, end);
                                if (!string.IsNullOrWhiteSpace(segment.Text))
                                {
                                    var entry = Record("Final", source.Track.Source, start, end, segment.Text, epoch, segmentId, attribution);
                                    if (entry is not null) lock (detailsGate)
                                    {
                                        try { details?.Append(labeled!); }
                                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                                        {
                                            details?.Dispose(); details = null; detailFailure = "Word details could not be saved; original transcript continues";
                                            log.Write("Word-detail write failure=" + ex.GetType().Name);
                                        }
                                    }
                                }
                            }
                            lock (gate) { source.Status = "Live · final segments saved"; source.Interim = segment.Final ? "" : segment.Text; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Write("Transcription source=" + source.Track.Source + "; failure=" + ex.GetType().Name);
                    disconnect = true; uncertain = Math.Min(finalThrough, sentThrough);
                    lock (gate) { source.Status = "Disconnected · possible missing words; reconnecting"; source.Interim = ""; }
                    if (finishing || ct.IsCancellationRequested || failure is not null)
                    {
                        if (failure is null) Record("Gap", source.Track.Source, uncertain, Math.Max(runEnd, sentThrough), "Stream interrupted before final confirmation.", epoch);
                        return;
                    }
                    if (ex is System.Net.WebSockets.WebSocketException && (ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal)))
                    {
                        lock (gate) source.Status = "Deepgram rejected key/access · recording continues";
                        // Remain alive until stop so the full omitted interval is logged.
                        while (!source.Queue.Reader.Completion.IsCompleted && !ct.IsCancellationRequested)
                        { while (source.Queue.Reader.TryRead(out _)) { } await Task.Delay(100, ct); }
                        Record("Gap", source.Track.Source, uncertain, Math.Max(runEnd, source.Through), "Deepgram rejected authorization; source was not transcribed.", epoch); return;
                    }
                }
                finally
                {
                    connectionStop.Cancel();
                    try { await receiving; } catch (Exception) { }
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, ++attempts)), ct);
            }
            if (disconnect || source.Through <= runStart) Record("Gap", source.Track.Source, uncertain, Math.Max(runEnd, source.Through), "Transcription ended without reconnecting.");
        }
        catch (OperationCanceledException)
        {
            if (failure is null) try { Record("Gap", source.Track.Source, uncertain, Math.Max(runEnd, source.Through), "Transcription stop timed out; final words may be missing."); } catch (Exception) { }
        }
        catch (Exception ex) { FailStorageOrSetup(ex); }
    }
}
