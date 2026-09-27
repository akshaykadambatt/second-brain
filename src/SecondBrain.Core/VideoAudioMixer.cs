using System.Buffers.Binary;

namespace SecondBrain.Core;

// The encoder worker owns the mixer. Packet timestamps, not arrival times, locate samples.
public sealed class VideoAudioMixer(double sessionStartSeconds)
{
    public const int SampleRate = 48000;
    private const int Capacity = SampleRate * 3;
    private readonly short[][] samples = [new short[Capacity], new short[Capacity]];
    private readonly long[][] stamps = [Enumerable.Repeat(-1L, Capacity).ToArray(), Enumerable.Repeat(-1L, Capacity).ToArray()];
    public long Through { get; private set; }
    public long LateSamples { get; private set; }
    public long MissingMicrophoneSamples { get; private set; }
    public long MissingSystemSamples { get; private set; }
    public int EstimatedPackets { get; private set; }
    public int Discontinuities { get; private set; }
    public static long SampleAt(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 13 * 3600) throw new InvalidDataException("Invalid audio/video clock.");
        return (long)Math.Round(seconds * SampleRate);
    }
    public static long VideoTime(long frame) => checked(frame * 10_000_000 / 30);
    public void Offer(AudioPacket packet, int rate)
    {
        if (rate is < 8000 or > 192000 || packet.Mono16.Length % 2 != 0 || packet.Mono16.Length > rate * 2 || !Enum.IsDefined(packet.Source)) throw new InvalidDataException("Invalid video audio packet.");
        var count = packet.Mono16.Length / 2;
        if (count == 0) return;
        var relative = packet.Seconds - sessionStartSeconds;
        var start = SampleAt(relative); var end = SampleAt(relative + count / (double)rate);
        if (end > Through + Capacity) throw new InvalidDataException("Audio is too far ahead of video encoding.");
        if (packet.EstimatedTime) EstimatedPackets++; if (packet.Discontinuity) Discontinuities++;
        LateSamples += Math.Max(0, Math.Min(end, Through) - Math.Max(start, 0));
        var source = (int)packet.Source;
        for (var frame = Math.Max(Through, Math.Max(start, 0)); frame < end; frame++)
        {
            var slot = (int)(frame % Capacity);
            if (stamps[source][slot] == frame) continue;
            var position = Math.Clamp((frame / (double)SampleRate - relative) * rate, 0, count - 1);
            var left = (int)position; var right = Math.Min(count - 1, left + 1); var fraction = position - left;
            var a = BinaryPrimitives.ReadInt16LittleEndian(packet.Mono16.AsSpan(left * 2)); var b = BinaryPrimitives.ReadInt16LittleEndian(packet.Mono16.AsSpan(right * 2));
            samples[source][slot] = packet.Silent ? (short)0 : (short)Math.Round(a + (b - a) * fraction); stamps[source][slot] = frame;
        }
    }
    public byte[] Read(int count)
    {
        if (count < 1 || count > SampleRate) throw new ArgumentOutOfRangeException(nameof(count));
        var output = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var frame = Through + i; var slot = (int)(frame % Capacity);
            var micPresent = stamps[0][slot] == frame; var systemPresent = stamps[1][slot] == frame;
            var mic = micPresent ? samples[0][slot] : 0; var system = systemPresent ? samples[1][slot] : 0;
            if (!micPresent) MissingMicrophoneSamples++; if (!systemPresent) MissingSystemSamples++;
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), (short)((mic + system) / 2));
        }
        Through += count; return output;
    }
}
