using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class CompanionTests
{
    private sealed class Provider : IAnswerProvider
    {
        internal sealed record Call(AssistantPrompt Prompt, Func<string, Task> Delta, TaskCompletionSource Done);
        public List<Call> Calls { get; } = [];
        public Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
        { var call = new Call(prompt, delta, new(TaskCreationOptions.RunContinuationsAsynchronously)); Calls.Add(call); return call.Done.Task; }
    }
    private static async Task Until(Func<bool> condition, int seconds = 4)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        await main.Knowledge!.Refresh(true);
        File.WriteAllText(Path.Combine(main.Knowledge.Root, "comfort.md"), "# Cedar\nCedar improves reading comfort by preserving text position. [[Home]]");
        await main.Knowledge.Refresh(true);
        var provider = new Provider();
        check(await main.StartCompanion(provider), "One Start listening starts the companion session");
        check(main.SessionStateText.Text == "LISTENING", "Live state confirms that the integrated session is listening");
        await Until(() => main.AssistantContext.SessionId != Guid.Empty);
        var companion = main.Companion!; var id = main.AssistantContext.SessionId; var connection = Guid.NewGuid();
        check(main.Recorder.State == RecordingState.Recording && main.Transcriber.Enabled && !main.Voice.Running && main.Panels.Count > 0 && main.Assistant is null, "One session records both sources, transcribes and opens reader without separate AI or voice windows");
        while (main.Panels.Count < 4) main.AddReader();
        var micConnection = Guid.NewGuid();
        companion.Observe(new(id, AudioSource.Microphone, micConnection, new(0, 1, "What should we do next?", true, true, .99f), AudioClock.Now));
        check(provider.Calls.Count == 0, "Microphone question does not generate an answer");
        companion.Observe(new(id, AudioSource.System, connection, new(0, 1, "What should we do next?", true, true, .99f), AudioClock.Now));
        check(provider.Calls.Count == 0, "Recent microphone speech echoed through system audio is suppressed");
        companion.Observe(new(id, AudioSource.Microphone, micConnection, new(1, .2, "does", false, false, .99f), AudioClock.Now));
        companion.Observe(new(id, AudioSource.System, connection, new(0, 1, "How does Cedar", true, false, .99f), AudioClock.Now));
        check(provider.Calls.Count == 0, "Split question waits for its speech endpoint");
        companion.Observe(new(id, AudioSource.System, connection, new(1, 1, "improve reading comfort?", true, true, .99f), AudioClock.Now));
        await Until(() => provider.Calls.Count == 1);
        check(provider.Calls.Count == 1 && provider.Calls[0].Prompt.Question == "How does Cedar improve reading comfort?", "Computer question reaches fast model with all final segments; a shared single microphone word cannot suppress it");
        var run = companion.Answers.Requests[0];
        await provider.Calls[0].Delta("Cedar keeps the text steady while");
        check(main.Session.Words.Count == 0, "Unfinished opening remains hidden");
        await provider.Calls[0].Delta(" you read."); provider.Calls[0].Done.SetResult();
        await Until(() => provider.Calls.Count == 2);
        check(main.Session.Answer == run.Fast && run.Fast.State == AnswerState.Receiving && main.Playback.Voice.Active && run.Deeper is null, "First sentence appears automatically with voice following and waits for continuation in the same answer");
        check(provider.Calls[1].Prompt.Opening == "Cedar keeps the text steady while you read." && provider.Calls[1].Prompt.Continuation, "Deeper model receives actual opening so it can continue without repeating");
        check(main.LiveSources.Items.Count > 0 && run.Knowledge!.Hits.Any(h => h.Chunk.File == "comfort.md") && provider.Calls[1].Prompt.Knowledge.Contains("comfort.md"), "Integrated answers retrieve vault context and display separate source rows");
        main.Playback.StartVoice();
        companion.Observe(new(id, AudioSource.Microphone, micConnection, new(1, .5, "", true, true, .99f), AudioClock.Now));
        companion.Observe(new(id, AudioSource.Microphone, micConnection, new(2, 1, "Cedar keeps the text steady", true, true, .99f), AudioClock.Now));
        check(main.Session.Position > 0, "Shared microphone transcript advances the teleprompter without a second capture connection");
        main.Session.Select(3);
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var word = main.Session.Words[3]; var ys = main.Panels.Select(p => p.WordScreenY(3)).ToArray();
        await provider.Calls[1].Delta("The deeper explanation adds useful detail without replacing the opening.\n\n");
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(main.Session.Words[3] == word && main.Session.Position == 3 && main.Panels.Select((p, i) => Math.Abs(p.WordScreenY(3) - ys[i])).All(d => d <= 1), "Continuation preserves active word identity and screen position within one pixel on four paused readers");
        provider.Calls[1].Done.SetResult(); await run.Work;
        check(run.Fast.State == AnswerState.Complete && run.Fast.Blocks.Count == 2 && run.FirstContinuationMs >= run.FirstReadableMs, "Opening and deeper continuation form one complete answer with separate timings");
        main.Playback.StartVoice();
        companion.Observe(new(id, AudioSource.System, connection, new(3, 1, "What is the next milestone?", true, true, .99f), AudioClock.Now));
        await Until(() => provider.Calls.Count == 3);
        check(provider.Calls.Count == 3 && main.Session.Answer == run.Fast, "New question keeps the previous text visible while waiting for its opening");
        await provider.Calls[2].Delta("The next milestone is a reading trial.\n\n");
        provider.Calls[2].Done.SetResult(); await Until(() => provider.Calls.Count == 4);
        var failed = companion.Answers.Requests[^1];
        check(main.Session.Answer == failed.Fast && main.Session.Position == 0 && main.Playback.Voice.Active, "New opening automatically switches the reader from an unfinished previous answer and resumes following");
        companion.Navigate(-1);
        check(main.Session.Answer == run.Fast && main.Session.Position == 3, "Previous answer restores the earlier reading position");
        main.Session.Select(main.Session.Words.Count); main.Playback.StartVoice(); companion.Tick();
        check(main.Session.Answer == run.Fast, "Revisiting the end of an earlier answer does not immediately jump forward again");
        main.Session.Select(3);
        await provider.Calls[3].Delta("Later details must remain with their own answer.\n\n");
        check(main.Session.Answer == run.Fast && main.Session.Position == 3, "Continuation arriving after Previous does not override manual navigation");
        provider.Calls[3].Done.SetException(new IOException("Injected continuation failure.")); await failed.Work;
        check(failed.Fast.State == AnswerState.Failed && failed.Fast.Blocks.Count == 2 && main.Session.Answer == run.Fast, "Failed deeper continuation retains readable text and does not affect the manually selected answer");
        companion.Ask("What should we test after this?");
        await Until(() => provider.Calls.Count == 5);
        main.LiveQuestion.Text = "Optional typed question";
        capture(main, Path.Combine(directory, "companion.png")); capture(main.Panels[0], Path.Combine(directory, "companion-reader.png"));
        var late = provider.Calls[4]; var stopping = main.StopCompanion();
        await late.Delta("This canceled late paragraph must not appear.\n\n"); late.Done.SetResult(); await stopping;
        check(!companion.Active && main.Recorder.State == RecordingState.Completed && AudioRecordingTests.SyntheticSource.OpenCount == 0 && !main.Playback.Voice.Active, "One stop cancels generation, saves recording/transcripts and releases both devices");
        check(!companion.Answers.Inbox.Answers.Any(a => a.Blocks.Any(b => b.Text.Contains("canceled late"))) && main.Session.Answer == run.Fast, "Stop rejects late output and leaves the current readable answer in place");
        check(File.Exists(Path.Combine(main.Recorder.LastDirectory!, "transcript.md")) && File.Exists(Path.Combine(main.Recorder.LastDirectory!, "system.wav")), "Combined session saves durable transcripts and both-source recording");
        check(Directory.GetFiles(main.Knowledge!.Root, "Summary.md", SearchOption.AllDirectories).Length == 1, "Stopping the combined session exports a linked vault summary automatically");
        check(await main.StartCompanion(new Provider()), "Combined session can restart after stop");
        await Until(() => main.AssistantContext.SessionId != id);
        check(main.Companion!.Answers.Requests.Count == 0 && main.Session.Words.Count == 0, "Restart has a new context session and does not replay old questions or answers");
        await main.StopCompanion();
        await CheckOutOfOrder(main, directory, check);
    }
    private static async Task CheckOutOfOrder(MainWindow main, string directory, Action<bool, string> check)
    {
        var reader = new ReaderSession(); var playback = new ReaderPlayback(reader); var provider = new Provider();
        using var companion = new CompanionSession(reader, playback, new(), new(main.Dispatcher, provider, new(directory)), new(Deeper: false));
        // This fixture controls question completions by call index. Forward
        // taps now request extensions, which have their own flowing fixture.
        companion.KeepFlowing = false;
        var older = companion.Ask("What is the earlier question?")!;
        var newer = companion.Ask("What is the latest question?")!;
        await Until(() => provider.Calls.Count == 2);
        await provider.Calls[1].Delta("Here is the newest answer ready to read.\n\n"); provider.Calls[1].Done.SetResult(); await newer.Work;
        reader.Select(2);
        await provider.Calls[0].Delta("This older opening arrived after the newer answer.\n\n"); provider.Calls[0].Done.SetResult(); await older.Work;
        check(reader.Answer == newer.Fast && reader.Position == 2, "Out-of-order older opening cannot replace the newest answer");
        companion.Navigate(-1); check(reader.Answer == older.Fast, "Late older answers remain available through Previous");
        playback.Pause();
        var latest = companion.Ask("What follows the latest question?")!;
        await provider.Calls[2].Delta("A partial opening without its ending");
        check(reader.Answer == older.Fast, "Incomplete new opening does not clear manually selected text");
        await provider.Calls[2].Delta(" is now complete.\n\n"); provider.Calls[2].Done.SetResult(); await latest.Work;
        check(reader.Answer == latest.Fast && reader.Position == 0 && playback.Voice.Active, "A subsequent new question takes over when ready even while reviewing a paused older answer");
        await companion.Stop();
    }
    public static async Task RunLive(MainWindow main, string directory, Action<bool, string> check)
    {
        var mic = Path.Combine(directory, "fixture-mic.wav"); var output = Path.Combine(directory, "fixture-system.wav");
        using (var speech = new SpeechSynthesizer())
        {
            speech.SetOutputToWaveFile(mic, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono)); speech.Speak("This is a microphone context test."); speech.SetOutputToNull();
            speech.SetOutputToWaveFile(output, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono)); speech.Speak("What is the project status and its next milestone?"); speech.SetOutputToNull();
        }
        var log = new DiagnosticLog(directory); var context = new AssistantContext();
        var transcriber = new RecordingTranscriber(new ApiKeyStore(directory).Load, log) { Enabled = true };
        var recorder = new RecordingService(Path.Combine(directory, "recordings"), log, (source, _) => new TranscriptionTests.WaveSource(source, source == AudioSource.Microphone ? mic : output), transcriber);
        using var provider = new OpenAiAnswerProvider(new ApiKeyStore(directory, "OpenAI").Load);
        using var companion = new CompanionSession(main.Session, main.Playback, context, new(main.Dispatcher, provider, log),
            new(Context: "Synthetic project Cedar has a blue status. Its next milestone is a reader trial on Friday. No other project facts are known."));
        var started = System.Diagnostics.Stopwatch.StartNew();
        double? firstDisplayedMs = null;
        try
        {
            main.AddReader(); await recorder.StartAsync("fixture", "fixture");
            while (started.Elapsed.TotalSeconds < 80)
            {
                var entries = transcriber.DrainAssistantEvents(out var dropped);
                foreach (var entry in entries) context.Observe(entry); if (dropped) context.MarkGap();
                foreach (var item in transcriber.DrainSpeech()) companion.Observe(item);
                companion.Tick();
                if (main.Session.Words.Count > 0) firstDisplayedMs ??= started.Elapsed.TotalMilliseconds;
                if (companion.Answers.Requests.LastOrDefault() is { Active: false }) break;
                await Task.Delay(50);
            }
            var run = companion.Answers.Requests.LastOrDefault();
            File.WriteAllText(Path.Combine(directory, "companion-latency.json"), JsonSerializer.Serialize(new { totalElapsedMs = started.Elapsed.TotalMilliseconds, firstDisplayedFromCaptureStartMs = firstDisplayedMs, run?.Question, run?.FirstTextMs, run?.FirstReadableMs, run?.FirstContinuationMs, run?.CompletedMs, run?.Status, State = run?.Fast.State.ToString(), text = main.Session.Text, physicalDevicesOpened = false }, new JsonSerializerOptions { WriteIndented = true }));
            check(run is not null, "Real Deepgram system stream detects the spoken synthetic question automatically");
            check(run!.Fast.State == AnswerState.Complete && run.FirstContinuationMs.HasValue, "Real OpenAI produces an opening and a deeper continuation: " + run.Status);
            check(main.Session.Answer == run.Fast && main.Session.Text.Contains("Friday", StringComparison.OrdinalIgnoreCase) && main.Session.Text.Contains("blue", StringComparison.OrdinalIgnoreCase), "Live opening and continuation appear automatically in one reader answer grounded in synthetic context");
        }
        finally { var stop = companion.Stop(); await recorder.StopAsync(); await stop; }
        check(recorder.State == RecordingState.Completed, "Real provider test saves audio/transcripts and stops cleanly without physical capture");
    }
}
