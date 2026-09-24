using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class TranscriptWindow : Window
{
    private sealed record Row(TranscriptDetail Detail)
    {
        public string Text => Detail.Text;
        public string Meta => $"{Detail.Source} · {TimeSpan.FromSeconds(Detail.Start):hh\\:mm\\:ss} · {Detail.Words.Length} timed words"
            + (Detail.Words.Any(w => w.SpeakerLabel is not null) ? " · " + string.Join(" / ", Detail.Words.Select(w => w.SpeakerLabel ?? "Unknown").Distinct()) : "");
    }
    internal TranscriptWindow(MainWindow owner, TranscriptDetail[] records, string warning, bool hidden)
    {
        InitializeComponent(); Owner = owner;
        if (hidden) { Opacity = 0; ShowActivated = false; }
        Summary.Text = $"{records.Length} finalized segments · {records.Sum(r => r.Words.Length)} timed words. Original transcript text is read-only. " + warning;
        Segments.ItemsSource = records.Select(r => new Row(r)).ToArray(); if (records.Length > 0) Segments.SelectedIndex = 0;
    }
    private void Segments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Segments.SelectedItem is not Row row) return;
        WordDetails.Text = row.Detail.Words.Length == 0 ? row.Detail.MetadataStatus : string.Join("\n", row.Detail.Words.Select(w =>
            $"{w.Start:F3}–{w.End:F3}s · {w.Text}" + (w.SpeakerLabel is null ? "" : " · " + w.SpeakerLabel)));
    }
}
