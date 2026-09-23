using System.Buffers.Binary;

namespace SecondBrain.Core;

public static class PcmAudio
{
    // Preserve the device sample rate; only convert sample representation and channel count.
    public static byte[] ToMono16(ReadOnlySpan<byte> input, int bits, int channels, bool floatingPoint, out int level)
    {
        if (channels < 1 || bits is not (16 or 24 or 32) || floatingPoint && bits != 32)
            throw new NotSupportedException("Unsupported microphone format. Choose another input device.");
        var sampleBytes = bits / 8;
        var frameBytes = sampleBytes * channels;
        if (input.Length % frameBytes != 0) throw new InvalidOperationException("Incomplete audio frame.");
        var output = new byte[input.Length / frameBytes * 2];
        double peak = 0;
        for (var i = 0; i < output.Length / 2; i++)
        {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = input.Slice(i * frameBytes + channel * sampleBytes, sampleBytes);
                var value = floatingPoint ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                    : bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d
                    : bits == 24 ? ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608d
                    : BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d;
                sum += double.IsFinite(value) ? value : 0;
            }
            var mono = Math.Clamp(sum / channels, -1, 1);
            peak = Math.Max(peak, Math.Abs(mono));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2, 2), (short)Math.Clamp(Math.Round(mono * 32768), -32768, 32767));
        }
        // A logarithmic meter makes normal quiet speech visible without amplifying the audio.
        level = peak < .00001 ? 0 : (int)Math.Clamp((20 * Math.Log10(peak) + 60) / 60 * 100, 0, 100);
        return output;
    }
}
