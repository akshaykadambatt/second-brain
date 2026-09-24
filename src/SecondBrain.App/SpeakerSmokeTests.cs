using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class SpeakerSmokeTests
{
    private sealed class Provider : IAnswerProvider
    {
        public Task Generate(AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation)
            => delta("A synthetic answer is available.\n\n");
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.LocalParticipantName.Text = "Morgan"; main.SpeakerSeparation.IsChecked = true; main.ConfigureSpeakers();
        check(!main.Recorder.HasSession && new SpeakerSettings(directory).Load().LocalParticipant == "Morgan", "Speaker preparation saves settings without starting capture");
        main.ContextVocabulary.Text = string.Join("\n", Enumerable.Range(0, 20).Select(i => new string('x', 50) + i));
        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        check(await main.StartCompanion(new Provider()), "Integrated listening starts with audio speaker settings");
        check(!main.SpeakerSettingsPanel.IsEnabled, "Active session freezes editable speaker preferences");
        main.Transcriber.SpeakerOptions = new(true, "Changed during capture");
        await Task.Delay(2300);
        check(main.Transcriber.View.Status.Contains("vocabulary") && main.Transcriber.View.Recent.Any(s => s.Contains("Speaker ")) && main.LiveMic.Text.Contains("Morgan"),
            "Live display shows provisional remote labels, configured microphone identity and vocabulary budget feedback");
        await main.StopCompanion();
        var path = main.Recorder.LastDirectory!; var words = TranscriptDetails.Read(path);
        var mic = words.Where(w => w.Source == AudioSource.Microphone).SelectMany(w => w.Words).ToArray();
        var remote = words.Where(w => w.Source == AudioSource.System && w.Words.Any(x => x.SpeakerId is not null)).ToArray();
        check(mic.Length > 0 && mic.All(w => w.SpeakerLabel == "Morgan") && remote.SelectMany(w => w.Words).Select(w => w.SpeakerId).Distinct().Count() >= 2,
            "Saved transcript keeps its original microphone identity and separates changing remote speakers");
        check(remote.Select(w => w.ConnectionId).Distinct().Count() >= 2 && remote.SelectMany(w => w.Words.Select(x => (w.ConnectionId, x.SpeakerId))).GroupBy(x => x.SpeakerId).All(g => g.Select(x => x.ConnectionId).Distinct().Count() == 1),
            "Reconnect cannot reuse an earlier connection's session speaker identity");
        check(remote.SelectMany(w => w.Words).All(w => w.SpeakerConfidence is null), "Streaming labels do not fabricate unavailable speaker confidence");
        var viewer = await main.OpenTranscript(path); viewer!.Segments.SelectedIndex = Array.FindIndex(words, w => w.Source == AudioSource.System && w.Words.Any(x => x.SpeakerId is not null));
        check(viewer.WordDetails.Text.Contains("Speaker "), "Meeting review exposes persisted speaker labels beside word times");
        capture(viewer, Path.Combine(directory, "speaker-transcript.png")); viewer.Close();
        main.SettingsTab.IsSelected = true; main.DevicesTab.IsSelected = true;
        capture(main, Path.Combine(directory, "speaker-settings.png"));
        main.LocalParticipantName.Text = "Riley"; main.ConfigureSpeakers();
        check(TranscriptDetails.Read(path).Where(w => w.Source == AudioSource.Microphone).SelectMany(w => w.Words).All(w => w.SpeakerLabel == "Morgan"),
            "Changing next-session speaker settings never rewrites a saved meeting");
    }
}
