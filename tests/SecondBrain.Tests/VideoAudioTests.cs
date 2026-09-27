using System.Buffers.Binary;
using SecondBrain.Core;

internal static class VideoAudioTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        static byte[] Tone(int rate, double seconds, short value) { var bytes = new byte[(int)(rate * seconds) * 2]; for (var i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), value); return bytes; }
        test("Video audio aligns mixed sample rates to clip start and never overwrites accepted overlap", () =>
        {
            var mix = new VideoAudioMixer(10.25);
            mix.Offer(new(AudioSource.Microphone, 10.20, Tone(44100, .2, 12000)), 44100);
            mix.Offer(new(AudioSource.System, 10.30, Tone(48000, .1, 8000)), 48000);
            mix.Offer(new(AudioSource.Microphone, 10.25, Tone(44100, .1, -12000)), 44100);
            var output = mix.Read(9600);
            short At(int frame) => BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(frame * 2));
            check(At(0) == 6000 && At(2399) == 6000 && At(2400) == 10000 && At(7199) == 10000 && At(7200) == 0, "Mixed source alignment or duplicate suppression changed");
            check(mix.Through == 9600 && mix.LateSamples == 0 && mix.MissingSystemSamples == 4800, "Padding or output accounting changed");
            mix.Offer(new(AudioSource.System, 10.25, Tone(48000, .1, 8000), EstimatedTime: true, Discontinuity: true), 48000);
            check(mix.LateSamples == 4800 && mix.EstimatedPackets == 1 && mix.Discontinuities == 1, "Late or estimated packets are not reported");
        });
        test("Audio and video share integer sample times across a synthetic two-hour clock", () =>
        {
            double maxError = 0;
            for (long frame = 0; frame <= 30 * 7200; frame++)
            {
                var audio = VideoAudioMixer.SampleAt(frame / 30d); var time = VideoAudioMixer.VideoTime(frame);
                maxError = Math.Max(maxError, Math.Abs(audio / 48000d - time / 10_000_000d));
                check(audio == frame * 1600, "Cumulative sample drift");
            }
            check(maxError < .000001, "Shared-clock conversion drifted");
            var mix = new VideoAudioMixer(7200.125); mix.Offer(new(AudioSource.Microphone, 7200.125, Tone(44100, .1, 16000)), 44100);
            check(BinaryPrimitives.ReadInt16LittleEndian(mix.Read(4800)) == 8000, "A late clip lost its source offset");
        });
        test("Video mixer bounds future packets and marks unavailable sources without pretending captured silence", () =>
        {
            var mix = new VideoAudioMixer(0); var rejected = false;
            try { mix.Offer(new(AudioSource.System, 10, Tone(48000, .1, 8000)), 48000); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unbounded future audio accepted");
            mix.Offer(new(AudioSource.Microphone, 0, Tone(48000, .1, short.MaxValue)), 48000); mix.Offer(new(AudioSource.System, 0, Tone(48000, .1, short.MaxValue)), 48000);
            check(BinaryPrimitives.ReadInt16LittleEndian(mix.Read(4800)) == short.MaxValue, "Mix clipped or overflowed");
            check(mix.Read(960).All(b => b == 0) && mix.MissingMicrophoneSamples == 960 && mix.MissingSystemSamples == 960, "Missing interval not padded and reported");
        });
    }
}
