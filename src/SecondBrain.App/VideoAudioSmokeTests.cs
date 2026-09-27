using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class VideoAudioSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        await Task.Factory.StartNew(() =>
        {
            var path = Path.Combine(directory, "synchronized.mp4");
            using (var writer = new NativeVideoWriter(path, 320, 180, true))
            {
                var pixels = new byte[320 * 180 * 4];
                for (var frame = 0; frame < 60; frame++)
                {
                    Array.Fill(pixels, frame < 30 ? (byte)0 : (byte)240); writer.Write(pixels, frame);
                    var pcm = new byte[3200];
                    if (frame is >= 30 and < 33) for (var i = 0; i < 1600; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(15000 * Math.Sin(2 * Math.PI * 1000 * (frame * 1600 + i) / 48000d)));
                    writer.WriteAudio(pcm, frame * 1600L);
                }
                writer.Complete();
            }
            var video = VideoSmokeTests.Decode(path); var audio = VideoSmokeTests.Decode(path, true);
            check(video.Count == 60 && audio.Audio.Length >= 48000 * 2 * 2 && VideoSmokeTests.Boxes(path).Contains("moof"), "Native fragmented MP4 contains decodable H.264 video and AAC audio");
            var firstSound = -1;
            for (var i = 0; i < audio.Audio.Length / 2; i++) if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.Audio.AsSpan(i * 2))) > 3000) { firstSound = i; break; }
            var drift = Math.Abs(firstSound / 48000d - 1);
            check(firstSound >= 0 && drift < .06 && Math.Abs(audio.EndTime - video.EndTime) < 800000, "Decoded sound marker and video clock remain synchronized within codec padding allowance");
            File.WriteAllText(Path.Combine(directory, "native-av-measurement.json"), JsonSerializer.Serialize(new { Synthetic = true, DurationSeconds = 2, SoundMarkerSeconds = firstSound / 48000d, MarkerErrorSeconds = drift, VideoEndSeconds = video.EndTime / 1e7, AudioEndSeconds = audio.EndTime / 1e7 }));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        capture(main, Path.Combine(directory, "synchronized-controls.png"));
    }
}
