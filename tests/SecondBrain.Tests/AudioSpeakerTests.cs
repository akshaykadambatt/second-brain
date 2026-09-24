using SecondBrain.Core;

internal static class AudioSpeakerTests
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        test("Streaming diarization pins v1 and encodes bounded vocabulary independently", () =>
        {
            var uri = StreamingSpeechOptions.Uri(48000, true, ["Cedar API", "a&diarize=false", "Cedar API"]).AbsoluteUri;
            check(uri.Contains("diarize_model=v1") && !uri.Contains("&diarize=") && uri.Contains("keyterm=Cedar%20API") && uri.Contains("a%26diarize%3Dfalse"), "Speech query settings are incorrect or injectable");
            check(!StreamingSpeechOptions.Uri(16000, false, []).Query.Contains("diarize"), "Audio-only mode still enabled diarization");
            var terms = StreamingSpeechOptions.Keyterms(Enumerable.Range(0, 100).Select(i => new string('x', 70) + i));
            check(terms.Length < 100 && terms.Sum(t => System.Text.Encoding.UTF8.GetByteCount(t) + 1) <= 400, "Vocabulary exceeded the conservative provider budget");
        });
        test("Audio speaker numbers are session-local and reconnects cannot impersonate prior speakers", () =>
        {
            var session = Guid.NewGuid(); var epoch = Guid.NewGuid(); var labels = new AudioSpeakers(session, new());
            TranscriptDetail Segment(Guid connection, int speaker) => new(1, session, Guid.NewGuid().ToString(), AudioSource.System, connection, 0, 1, "Hello.", [new("w", "Hello.", 0, 1, .9f, speaker)]);
            var first = labels.Label(Segment(epoch, 0)).Words[0]; var repeat = labels.Label(Segment(epoch, 0)).Words[0];
            var other = labels.Label(Segment(epoch, 1)).Words[0]; var reconnect = labels.Label(Segment(Guid.NewGuid(), 0)).Words[0];
            check(first.SpeakerLabel == "Speaker 1" && first.SpeakerId == repeat.SpeakerId && first.SpeakerId != other.SpeakerId && first.SpeakerId != reconnect.SpeakerId,
                "Speaker labels were merged across changes or reconnects");
            var nextSession = Guid.NewGuid(); var next = new AudioSpeakers(nextSession, new()).Label(Segment(epoch, 0) with { SessionId = nextSession }).Words[0];
            check(next.SpeakerId != first.SpeakerId, "Speaker identity leaked between sessions");
        });
        test("Missing, weak and visibly overlapping audio metadata remains Unknown", () =>
        {
            var id = Guid.NewGuid(); var labels = new AudioSpeakers(id, new());
            var segment = new TranscriptDetail(1, id, "s", AudioSource.System, Guid.NewGuid(), 0, 2, "Overlapping.",
                [new("a", "One", 0, 1, .9f, 0), new("b", "Two", .5, 1.5, .9f, 1), new("c", "Weak", 1.5, 1.7, .2f, 2), new("d", "Missing", 1.7, 2)]);
            check(labels.Label(segment).Words.All(w => w.SpeakerLabel == "Unknown" && w.SpeakerId is null), "Uncertain words were assigned identities");
            check(labels.Label(segment with { Words = [] }).Words.Length == 0, "Silence created a speaker");
            var mic = new AudioSpeakers(id, new(LocalParticipant: "Morgan")).Label(segment with { Source = AudioSource.Microphone });
            check(mic.Words.All(w => w.SpeakerLabel == "Morgan"), "Configured microphone identity was not retained");
            check(new AudioSpeakers(id, new(false)).Label(segment).Words.All(w => w.SpeakerLabel == "Unknown"), "Disabled separation still assigned remote labels");
        });
        test("Speaker settings persist without capture and reject invalid replacement", () =>
        {
            var settings = new SpeakerSettings(folder("speaker-options")); settings.Save(new(true, "Morgan"));
            try { settings.Save(new(true, "Invalid\nName")); throw new Exception("Invalid speaker name accepted"); } catch (InvalidDataException) { }
            check(settings.Load().LocalParticipant == "Morgan", "Invalid update destroyed the prior speaker settings");
        });
    }
}
