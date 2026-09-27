using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using static SecondBrain.App.NativeVideoWriter;

namespace SecondBrain.App;

internal static class VideoSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        await Task.Factory.StartNew(() =>
        {
            var path = Path.Combine(directory, "native-video.mp4");
            using (var writer = new NativeVideoWriter(path, 320, 180))
            {
                var pixels = new byte[320 * 180 * 4];
                for (var y = 0; y < 180; y++) for (var x = 0; x < 320; x++) { var p = (y * 320 + x) * 4; pixels[p + (y < 90 ? 2 : 0)] = 240; pixels[p + 3] = 255; }
                for (var i = 0; i < 60; i++) writer.Write(pixels, i);
                writer.Complete();
            }
            check(Boxes(path).Contains("moof"), "Native encoder writes fragmented MP4");
            var decoded = Decode(path);
            check(decoded.Count == 60 && Math.Abs(decoded.LastTime - 59 * 10_000_000L / 30) < 2, "Native decoder reads all frames at the expected 30 fps timestamps");
            check(decoded.First is { Length: >= 230400 } && decoded.First[2] > 170 && decoded.First[0] < 70 && decoded.First[(179 * 320) * 4] > 170, "Decoded top and bottom colors retain top-down frame orientation");
            var rejected = false; try { using var duplicate = new NativeVideoWriter(path, 320, 180); } catch (IOException) { rejected = true; }
            check(rejected, "Recording cannot overwrite an existing clip");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var selection = new VideoSelection(new(1, 320, 180, "Synthetic display"), false);
        check(new VideoSelection(new(1, 3840, 2160, "4k"), false).Size == (1920, 1080) && new VideoSelection(new(1, 1080, 1920, "Portrait"), false).Size == (606, 1080), "Default sizing fits the whole landscape or portrait display without cropping");
        var before = main.Recorder.LastDirectory;
        check(!await main.StartVideo(null) && main.Recorder.LastDirectory == before && main.Companion?.Active != true, "Cancelling selection does not start audio or video");
        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        check(await main.StartVideo(selection, new Provider(), (_, receive, _) => new Frames(receive)), "Selecting video while idle starts integrated audio and the native encoder");
        await Task.Delay(600); var video = main.VideoRecording!; var session = main.Recorder.LastDirectory;
        await main.StopVideo();
        check(video.FrameCount > 0 && video.Error is null && main.Companion?.Active == true && main.Recorder.State == RecordingState.Recording, "Stopping video finalizes the clip while listening continues");
        check(Boxes(video.Path).Contains("moof") && File.Exists(Path.ChangeExtension(video.Path, ".json")), "Display pipeline produces fragmented video and meeting-relative metadata");
        check(await main.StartVideo(selection, null, (_, receive, _) => new Frames(receive)), "A second clip reuses the same listening session");
        await Task.Delay(200); var second = main.VideoRecording!; await main.StopCompanion();
        check(second.Error is null && !second.Active && main.Recorder.State == RecordingState.Completed && main.Recorder.LastDirectory == session, "Stopping listening finalizes video without replacing the session");
        capture(main, Path.Combine(directory, "video-controls.png"));
    }
    private sealed class Provider : SecondBrain.Core.IAnswerProvider
    { public Task Generate(SecondBrain.Core.AssistantPrompt prompt, Func<string, Task> delta, CancellationToken cancellation) => Task.CompletedTask; }
    private sealed class Frames : IDisplayCapture
    {
        private readonly Timer timer;
        internal Frames(Action<DisplayFrame> observe)
        {
            var pixels = new byte[320 * 180 * 4]; Array.Fill(pixels, (byte)120);
            timer = new Timer(_ => observe(new(AudioClock.Now, 320, 180, pixels)), null, 0, 33);
        }
        public void Dispose() => timer.Dispose();
    }
    internal static HashSet<string> Boxes(string path)
    {
        var boxes = new HashSet<string>(); using var stream = File.OpenRead(path); var header = new byte[8];
        while (stream.Read(header) == 8)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header); boxes.Add(System.Text.Encoding.ASCII.GetString(header, 4, 4));
            if (size < 8 || size > stream.Length - stream.Position + 8) break; stream.Seek(size - 8, SeekOrigin.Current);
        }
        return boxes;
    }
    internal static (int Count, long LastTime, byte[]? First) Decode(string path)
    {
        nint reader = 0, type = 0, attrs = 0; Hr(CoInitializeEx(0, 0)); Hr(MFStartup(0x20070, 0));
        try
        {
            Hr(MFCreateAttributes(out attrs, 1)); Set32(attrs, new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), 1);
            Hr(MFCreateSourceReaderFromURL(path, attrs, out reader)); Hr(MFCreateMediaType(out type)); SetGuid(type, Major, Video); SetGuid(type, Subtype, Rgb32);
            Hr(Method<ReaderType>(reader, 7)(reader, 0xfffffffc, 0, type));
            var count = 0; long last = 0; byte[]? first = null;
            while (count < 10000)
            {
                Hr(Method<ReadSample>(reader, 9)(reader, 0xfffffffc, 0, out _, out var flags, out var time, out var sample));
                try
                {
                    if ((flags & 2) != 0) break;
                    if (sample == 0) continue; count++; last = time;
                    if (first is null)
                    {
                        Hr(Method<Contiguous>(sample, 41)(sample, out var buffer));
                        try { Hr(Method<LockBuffer>(buffer, 3)(buffer, out var pointer, out _, out var length)); try { first = new byte[length]; Marshal.Copy(pointer, first, 0, (int)length); } finally { Hr(Method<Simple>(buffer, 4)(buffer)); } }
                        finally { Release(buffer); }
                    }
                }
                finally { Release(sample); }
            }
            return (count, last, first);
        }
        finally { Release(type); Release(reader); Release(attrs); MFShutdown(); CoUninitialize(); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReaderType(nint self, uint index, nint reserved, nint type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSample(nint self, uint index, uint control, out uint actual, out uint flags, out long time, out nint sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Contiguous(nint self, out nint buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string path, nint attributes, out nint reader);
}

