using System.Speech.Recognition;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class VoiceService(Dispatcher dispatcher, ReaderSession session)
{
    private SpeechRecognitionEngine? engine;
    private int generation;
    private bool ignoreCurrentUtterance;
    private Task pendingStop = Task.CompletedTask;
    private SpeechFollower follower = new(session);
    public bool Running { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? Heard;
    public event Action<int>? Level;
    public event Action? Stopped;

    public void Reanchor()
    {
        follower.BeginUtterance();
        ignoreCurrentUtterance = Running;
        if (Running) Status?.Invoke("Position changed. Pause briefly, then read from the selected word.");
    }

    public async Task StartAsync(string? waveFile = null)
    {
        await StopAsync();
        var token = ++generation;
        follower = new SpeechFollower(session);
        follower.BeginUtterance();
        ignoreCurrentUtterance = false;
        Status?.Invoke("Starting Windows speech recognition…");
        SpeechRecognitionEngine? candidate = null;
        try
        {
            candidate = await Task.Run(() =>
            {
                var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(x => x.Culture.TwoLetterISOLanguageName == "en")
                    ?? throw new InvalidOperationException("No English Windows speech recognizer is installed. Add English speech in Windows Settings → Time & language → Speech.");
                var recognizer = new SpeechRecognitionEngine(info);
                try
                {
                    recognizer.LoadGrammar(new DictationGrammar());
                    recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(350);
                    recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(500);
                    if (waveFile is null) recognizer.SetInputToDefaultAudioDevice();
                    else recognizer.SetInputToWaveFile(waveFile);
                    return recognizer;
                }
                catch { recognizer.Dispose(); throw; }
            });
            if (token != generation) { candidate.Dispose(); return; }
            engine = candidate;
            void Dispatch(Action action) => dispatcher.BeginInvoke(() => { if (token == generation) action(); });
            candidate.SpeechDetected += (_, _) => Dispatch(() => { ignoreCurrentUtterance = false; follower.BeginUtterance(); });
            candidate.SpeechHypothesized += (_, e) => Dispatch(() => Receive(e.Result.Text, false, e.Result.Confidence));
            candidate.SpeechRecognized += (_, e) => Dispatch(() => Receive(e.Result.Text, true, e.Result.Confidence));
            candidate.AudioLevelUpdated += (_, e) => Dispatch(() => Level?.Invoke(e.AudioLevel));
            candidate.SpeechRecognitionRejected += (_, _) => Dispatch(() => Status?.Invoke("Couldn't match that phrase. Holding position; click a word to resume there."));
            candidate.RecognizeCompleted += (_, e) => Dispatch(() =>
            {
                Running = false;
                Status?.Invoke(e.Error is null ? "Listening stopped." : "Microphone stopped: " + e.Error.Message);
                Stopped?.Invoke();
                _ = StopAsync();
            });
            candidate.RecognizeAsync(RecognizeMode.Multiple);
            Running = true;
            Status?.Invoke("Listening · " + candidate.RecognizerInfo.Culture.Name + " · Windows default microphone");
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            if (token != generation) return;
            engine = null;
            Running = false;
            Status?.Invoke("Cannot start listening: " + ex.Message + " Check your default microphone and Windows microphone permissions.");
            Stopped?.Invoke();
        }
    }

    private void Receive(string text, bool final, float confidence)
    {
        Heard?.Invoke(text);
        if (ignoreCurrentUtterance) return;
        var moved = follower.Observe(text, final, confidence);
        Status?.Invoke(session.Position == session.Words.Count ? "Finished. Reset to read again." : moved ? "Following your voice…" : "Listening · waiting for a clear match");
    }

    public Task StopAsync()
    {
        generation++;
        Running = false;
        var old = engine;
        engine = null;
        if (old is not null)
            pendingStop = Task.Run(() => { try { old.RecognizeAsyncCancel(); } catch (InvalidOperationException) { } finally { old.Dispose(); } });
        Level?.Invoke(0);
        return pendingStop;
    }
}
