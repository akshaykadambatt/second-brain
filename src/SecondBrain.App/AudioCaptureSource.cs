using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record AudioDevice(string Id, string Name);
internal sealed record CapturedAudio(double ClockSeconds, byte[] Pcm, int Level, bool Silent, bool Discontinuity, bool EstimatedTime);
internal interface IAudioCaptureSource : IDisposable
{
    AudioTrack Track { get; }
    void Start();
    void Drain(Action<CapturedAudio> received);
}
internal static class AudioClock
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

// NAudio owns WASAPI interop. Read packet timestamps directly instead of assigning
// callback-arrival times, which would add independent buffering delays to each track.
internal sealed class AudioCaptureSource : IAudioCaptureSource
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice device;
    private readonly AudioClient client;
    private readonly AudioCaptureClient capture;
    private readonly WaveFormat format;
    private readonly bool floating;
    private bool started;
    private double lastHealth;
    public AudioTrack Track { get; }
    public static IReadOnlyList<AudioDevice> Devices(AudioSource source)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        string? defaultId = null;
        try { using var device = enumerator.GetDefaultAudioEndpoint(source == AudioSource.Microphone ? DataFlow.Capture : DataFlow.Render, source == AudioSource.Microphone ? Role.Communications : Role.Multimedia); defaultId = device.ID; }
        catch (COMException) { }
        foreach (var item in enumerator.EnumerateAudioEndPoints(source == AudioSource.Microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
            using (item) devices.Add(new(item.ID, item.FriendlyName + (item.ID == defaultId ? " (Windows default)" : "")));
        return devices.OrderByDescending(d => d.Id == defaultId).ToArray();
    }
    public AudioCaptureSource(AudioSource source, string id)
    {
        enumerator = new();
        MMDevice? opened = null; AudioClient? audio = null;
        try
        {
            opened = enumerator.GetDevice(id); device = opened;
            if (device.State != DeviceState.Active || device.DataFlow != (source == AudioSource.Microphone ? DataFlow.Capture : DataFlow.Render))
                throw new InvalidOperationException("Selected device is unavailable or has the wrong direction.");
            audio = device.AudioClient; client = audio; format = client.MixFormat;
            floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
                || format is WaveFormatExtensible extensible && !floating && extensible.SubFormat != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                throw new NotSupportedException("Unsupported device format.");
            PcmAudio.ToMono16([], format.BitsPerSample, format.Channels, floating, out _);
            client.Initialize(AudioClientShareMode.Shared, source == AudioSource.System ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
                1_000_000, 0, format, Guid.Empty);
            capture = client.AudioCaptureClient;
            Track = new(source, id, device.FriendlyName, format.SampleRate);
        }
        catch { audio?.Dispose(); opened?.Dispose(); enumerator.Dispose(); throw; }
    }
    public void Start() { client.Start(); started = true; }
    public void Drain(Action<CapturedAudio> received)
    {
        var now = AudioClock.Now;
        if (now - lastHealth > 1)
        { lastHealth = now; if (device.State != DeviceState.Active) throw new InvalidOperationException("Device disconnected."); }
        while (capture.GetNextPacketSize() > 0)
        {
            var pointer = capture.GetBuffer(out var frames, out var flags, out _, out var qpc);
            CapturedAudio packet;
            try
            {
                var silent = (flags & AudioClientBufferFlags.Silent) != 0;
                var data = new byte[frames * format.BlockAlign];
                if (!silent) Marshal.Copy(pointer, data, 0, data.Length);
                var pcm = PcmAudio.ToMono16(data, format.BitsPerSample, format.Channels, floating, out var level);
                var estimated = (flags & AudioClientBufferFlags.TimestampError) != 0 || qpc <= 0;
                packet = new(estimated ? AudioClock.Now - frames / (double)format.SampleRate : qpc / 10_000_000d,
                    pcm, level, silent, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0, estimated);
            }
            finally { capture.ReleaseBuffer(frames); }
            received(packet);
        }
    }
    public void Dispose()
    {
        try { if (started) client.Stop(); }
        finally { capture.Dispose(); client.Dispose(); device.Dispose(); enumerator.Dispose(); }
    }
}
