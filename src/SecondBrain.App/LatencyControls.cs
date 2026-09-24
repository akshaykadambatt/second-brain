using System.IO;
using System.Windows;
using Microsoft.Win32;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private AnswerLatency[] CurrentTiming() => Companion?.Answers.Requests.Select(r => r.Latency).ToArray() ?? [];
    private void RefreshLatency()
    {
        var samples = CurrentTiming(); ExportTiming.IsEnabled = samples.Length > 0;
        if (samples.Length == 0) { TimingSummary.Text = "No answer timings yet."; return; }
        static string Ms(double? value) => value is { } ms ? $"{ms:F0} ms" : "unavailable";
        static string Group(string name, IEnumerable<AnswerLatency> samples)
        {
            var s = LatencyReport.Summarize(samples);
            return $"{name}: {s.Readable}/{s.Requests} readable · speech-timed {s.SpeechTimed} · speech p50 / p95 {Ms(s.SpeechP50)} / {Ms(s.SpeechP95)} · request p50 / p95 {Ms(s.RequestP50)} / {Ms(s.RequestP95)}";
        }
        var last = samples[^1];
        TimingSummary.Text = $"Latest · speech to readable {Ms(last.SpeechToReadableMs)} · request to readable {Ms(last.RequestToReadableMs)}\n"
            + $"Transcription {Ms(last.TranscriptionMs)} · detection {Ms(last.DetectionMs)} · queue {Ms(last.QueueMs)} · retrieval {Ms(last.RetrievalMs)} · generation to readable {Ms(last.GenerationToReadableMs)}\n"
            + Group("Cold session (first request)", samples.Where(s => s.FirstInSession)) + "\n"
            + Group("Warm session (later requests)", samples.Where(s => !s.FirstInSession));
    }
    internal async Task<bool> SaveCompanionTiming(string recordingDirectory)
    {
        // Snapshot on the UI thread before the background write; no transcript or prompt text in this sidecar.
        var samples = CurrentTiming();
        try
        {
            await Task.Run(() =>
            {
                var id = RecordingSession.ReadManifest(recordingDirectory).Id;
                LatencyReport.Create(samples.Where(s => s.SessionId == id)).Save(Path.Combine(recordingDirectory, "latency.json"));
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { log.Write("Latency report save failure=" + ex.GetType().Name); return false; }
    }
    private void ExportTiming_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Timing report|*.json", FileName = "answer-timings.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { LatencyReport.Create(CurrentTiming()).Save(dialog.FileName); TimingMessage.Text = "Timing report saved."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { TimingMessage.Text = "Could not save the timing report. Choose another writable location."; }
    }
}
