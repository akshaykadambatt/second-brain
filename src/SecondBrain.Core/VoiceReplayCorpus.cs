namespace SecondBrain.Core;

// Recognition-output fixtures, not fabricated accuracy measurements. These
// events exercise the same source gate, matcher and flow clock used by Deepgram.
public static class VoiceReplayCorpus
{
    public sealed record Event(double At, SpeechSegment Segment, int Position, AudioSource Source = AudioSource.Microphone);
    public sealed record Trial(string Name, string Script, Event[] Events);
    public static IReadOnlyList<Trial> Trials { get; } =
    [
        new("Fillers, revision, duplicate and pause", "Today we review the release plan and test the reader.",
        [new(.1, new(0, .4, "um today we", false, false, .95f), 0),
         new(.3, new(0, .7, "today we review", false, false, .95f), 2),
         new(.5, new(0, 1, "today we review", true, true, .95f), 3),
         new(.8, new(0, 1, "today we review", true, true, .95f), 3),
         new(2.8, new(1, 1, "uh the release plan", true, true, .95f), 6)]),
        new("Skipped word and off-script return", "We should carefully test the reader before the meeting starts today.",
        [new(.1, new(0, 1.2, "we should test the reader", true, true, .95f), 6),
         new(.5, new(1.2, .8, "my coffee is getting cold", true, true, .95f), 6),
         new(1.5, new(2, 1, "before the meeting starts", true, true, .95f), 10)]),
        new("Ambiguous repetition holds then distinctive return", "Intro words now red green blue then red green blue final distinctive closing phrase.",
        [new(.1, new(0, 1, "red green blue", true, true, .95f), 0),
         new(.6, new(1, 1, "final distinctive closing phrase", true, true, .95f), 14)]),
        new("Exact repeated phrase at anchor", "Hello world hello world welcome back.",
        [new(.1, new(0, 1, "hello world", true, false, .95f), 2),
         new(.3, new(0, 1, "hello world", true, false, .95f), 2),
         new(.8, new(1, 1, "hello world", true, false, .95f), 4),
         new(1.1, new(0, 2, "hello world welcome back", true, true, .95f), 4)]),
        new("Technical tokens and punctuation", "Deepgram Nova-3 sends PCM16 through WASAPI. The API response uses JSON.",
        [new(.1, new(0, 1, "Deepgram Nova3 sends PCM16", true, true, .95f), 4),
         new(.6, new(1, 1, "through wasapi the api response uses json", true, true, .95f), 11)]),
        new("Low confidence and remote audio never own progress", "We can ship this version after the checks pass.",
        [new(.1, new(0, 1, "we can ship this version", true, true, .99f), 0, AudioSource.System),
         new(.3, new(0, 1, "we can ship this version", false, false, .1f), 0),
         new(.5, new(0, 1, "we can ship this version", true, true, .1f), 0),
         new(1, new(1, 1, "we can ship this version", true, true, .95f), 5)]),
        new("Short phrase ahead cannot teleport", "Intro words here some more context before we begin. Good morning everyone.",
        [new(.1, new(0, .6, "good morning", true, true, .98f), 0),
         new(.5, new(.6, 1, "good morning everyone", true, true, .98f), 12)])
    ];
}
