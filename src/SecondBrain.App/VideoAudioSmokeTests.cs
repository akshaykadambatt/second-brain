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
        var key = Path.Combine(directory, "synthetic-key.txt"); File.WriteAllText(key, "synthetic-deepgram-key-for-offline-tests");
        try { new ApiKeyStore(directory).Import(key); } finally { File.Delete(key); }
        var provider = new VideoSmokeTests.Provider(); check(await main.StartCompanion(provider), "Audio session starts before optional video");
        var session = main.Recorder.LastDirectory;
        Action<AudioPacket, int> failedObserver = (_, _) => throw new IOException("Synthetic optional subscriber failure");
        main.Recorder.AudioForVideo += failedObserver;
        var selection = new VideoSelection(new(1, 320, 180, "Synthetic display"), false);
        await Task.Delay(300);
        check(await main.StartVideo(selection, null, (_, receive, _) => new VideoSmokeTests.Frames(receive)), "Video joins an already running session");
        check(AudioRecordingTests.SyntheticSource.OpenCount == 2 && main.Recorder.LastDirectory == session, "Video reuses exactly the existing two audio sources");
        await Task.Delay(1000); var clip = main.VideoRecording!; await main.StopVideo();
        check(clip.AudioPacketsReceived > 20 && clip.Error is null && main.Recorder.State == RecordingState.Recording, "Packet fan-out failure cannot stop recording; video audio drains independently");
        await Task.Factory.StartNew(() =>
        {
            var decoded = VideoSmokeTests.Decode(clip.Path, true);
            var audible = Enumerable.Range(0, decoded.Audio.Length / 2).Count(i => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(decoded.Audio.AsSpan(i * 2))) > 1000);
            check(audible > 4800, "Saved video contains audible existing-session microphone and system packets");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using (var info = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(clip.Path, ".json"))))
        {
            var root = info.RootElement;
            check(root.GetProperty("Audio").GetBoolean() && root.GetProperty("AudioFrames").GetInt64() == clip.FrameCount * 1600 && root.GetProperty("SessionStartSeconds").GetDouble() > .2, "Clip metadata records the shared session offset and identical audio/video duration");
        }
        check(await main.StartVideo(selection, null, (_, receive, _) => new VideoSmokeTests.Frames(receive)), "Video can restart in the same audio session");
        await Task.Delay(200); await main.StopCompanion();
        check(!main.VideoRecording!.Active && main.VideoRecording.Error is null && AudioRecordingTests.SyntheticSource.OpenCount == 0, "Stop listening drains and finalizes both media streams without retained devices");
        check(await main.StartVideo(selection, provider, (_, receive, _) => new VideoSmokeTests.Frames(receive)), "Exit fixture starts a fresh integrated A/V session");
        await Task.Delay(200); var final = main.VideoRecording!; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; main.ExitApplication();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12))) while (!main.ShutdownCompleted) await Task.Delay(20, timeout.Token);
        check(!final.Active && final.Error is null && AudioRecordingTests.SyntheticSource.OpenCount == 0, "Tray exit awaits final A/V mux and releases both original capture sources");
    }
}
