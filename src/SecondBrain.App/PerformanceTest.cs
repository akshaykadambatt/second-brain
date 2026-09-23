using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace SecondBrain.App;

internal static class PerformanceTest
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    public static async Task Run(MainWindow window, string directory, int durationSeconds = 1200)
    {
        var intervals = new List<double>(100000);
        var clock = Stopwatch.StartNew();
        var previous = 0d;
        var reversals = 0;
        var lastOffsets = new double[4];
        var lastWord = 0;
        var previousCursor = 0d;
        var maxCursorStep = 0d;
        var initialMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        void Sample(object? sender, EventArgs args)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (previous > 0) intervals.Add(now - previous);
            previous = now;
            for (var i = 0; i < window.Panels.Count; i++)
            {
                var offset = window.Panels[i].ScrollPosition;
                if (offset + .01 < lastOffsets[i]) reversals++;
                lastOffsets[i] = offset;
            }
            if (window.Session.Position < lastWord) reversals++;
            lastWord = window.Session.Position;
            maxCursorStep = Math.Max(maxCursorStep, window.Playback.Cursor - previousCursor);
            previousCursor = window.Playback.Cursor;
        }
        try
        {
            // Prevent automatic sleep during this bounded measurement, then restore normal policy.
            SetThreadExecutionState(0x80000003);
            window.ScriptEditor.Text = string.Join(" ", Enumerable.Repeat(Core.ReaderSession.Sample, 25));
            await window.ApplyText();
            while (window.Panels.Count < 4) window.AddReader();
            for (var i = 0; i < 4; i++) { window.Panels[i].Width = 440 + 50 * i; window.Panels[i].Height = 430; }
            window.Playback.SetSpeed(150);
            await window.ToggleTimed();
            await Task.Delay(3000);
            previousCursor = window.Playback.Cursor;
            for (var i = 0; i < 4; i++) lastOffsets[i] = window.Panels[i].ScrollPosition;
            clock.Restart();
            CompositionTarget.Rendering += Sample;
            while (clock.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                await Task.Delay(1000);
                var partial = new { elapsedSeconds = clock.Elapsed.TotalSeconds, frames = intervals.Count, word = window.Session.Position,
                    running = window.Playback.Playing, microphone = window.Voice.Running, reversals };
                File.WriteAllText(Path.Combine(directory, "performance-progress.json"), JsonSerializer.Serialize(partial));
                if (!window.Playback.Playing || window.Voice.Running) throw new InvalidOperationException("Timed run stopped early or microphone unexpectedly active.");
            }
            CompositionTarget.Rendering -= Sample;
            window.Playback.Pause();
            var sorted = intervals.Order().ToArray();
            var p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            var report = new { passed = p95 < 25 && reversals == 0 && sorted.Length >= durationSeconds * 25,
                durationSeconds = clock.Elapsed.TotalSeconds, panels = 4, frames = sorted.Length, p95Milliseconds = p95,
                medianMilliseconds = sorted[sorted.Length / 2], maxMilliseconds = sorted[^1], intervalsOver25 = sorted.Count(i => i >= 25),
                reversals, maxCursorStep, startPrivateBytes = initialMemory, endPrivateBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                observedMode = "Hidden WPF windows; rendering-event timing, not visible-display presentation timing", microphoneActive = window.Voice.Running };
            File.WriteAllText(Path.Combine(directory, "performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
        catch (Exception ex)
        { File.WriteAllText(Path.Combine(directory, "performance.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); Application.Current.Shutdown(1); }
        finally { CompositionTarget.Rendering -= Sample; SetThreadExecutionState(0x80000000); }
    }
}
