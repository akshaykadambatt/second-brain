using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record SavedVideo(string Path, string State, double? Start, double? Duration, bool Audio, string Notice)
{
    public string Label => (Start is { } start ? "From " + TimeSpan.FromSeconds(start).ToString(@"hh\:mm\:ss") : "Start time unavailable") + " · " + State
        + (Duration is { } duration ? " · " + TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss") : " · duration needs review");
    internal static SavedVideo[] List(string directory)
    {
        directory = LocalBackup.Root(directory);
        return Directory.EnumerateFiles(directory, "video-*.mp4").Take(1000).Select(path =>
        {
            path = LocalBackup.Root(path);
            try
            {
                var metadata = LocalBackup.Root(System.IO.Path.ChangeExtension(path, ".json"));
                if (new FileInfo(metadata).Length > 1_000_000) throw new InvalidDataException("Video metadata is too large.");
                using var doc = JsonDocument.Parse(File.ReadAllText(metadata)); var root = doc.RootElement;
                double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 && n <= 86400 ? n : null;
                string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "")[..Math.Min(1000, value.GetString()!.Length)] : "";
                return new SavedVideo(path, Text("State"), Number("SessionStartSeconds"), Number("DurationSeconds"), root.TryGetProperty("Audio", out var audio) && audio.ValueKind == JsonValueKind.True, Text("Error") + " " + Text("RecoveryNotice"));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            { return new SavedVideo(path, "Metadata unavailable", null, null, false, "Open the original or recover its complete fragments; no timing is assumed."); }
        }).OrderBy(v => v.Start ?? double.MaxValue).ThenBy(v => v.Path).ToArray();
    }
}

internal sealed class VideoLibraryView : UserControl
{
    private readonly MainWindow main;
    private readonly string directory;
    private readonly Action? beforeMediaAccess;
    internal ListBox Clips { get; } = new() { DisplayMemberPath = "Label", MinHeight = 120 };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly WrapPanel actions = new() { Margin = new Thickness(0, 12, 0, 0) };
    internal VideoLibraryView(MainWindow main, string directory, Action? beforeMediaAccess = null)
    {
        this.main = main; this.directory = directory; this.beforeMediaAccess = beforeMediaAccess;
        var grid = new Grid { Margin = new Thickness(20) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new() { Height = height });
        var description = new TextBlock { Text = "Saved video clips\nRecovery creates a separate copy from complete fragments. Stop listening first; original files are preserved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        grid.Children.Add(description); Grid.SetRow(Clips, 1); grid.Children.Add(Clips); Grid.SetRow(actions, 2); grid.Children.Add(actions); Grid.SetRow(Status, 3); grid.Children.Add(Status); Content = grid;
        Button Button(string title) { var b = new Button { Content = title, Margin = new Thickness(0, 0, 8, 6) }; actions.Children.Add(b); return b; }
        Button("Open video").Click += (_, _) => Open();
        Button("Recover a copy").Click += async (_, _) => await RecoverSelected();
        Button("Refresh clips").Click += (_, _) => Refresh();
        Clips.SelectionChanged += (_, _) => { if (Clips.SelectedItem is SavedVideo video) Status.Text = video.Notice.Trim(); };
        Refresh();
    }
    internal void Refresh()
    {
        try { Clips.ItemsSource = SavedVideo.List(directory); Clips.SelectedIndex = 0; if (Clips.Items.Count == 0) Status.Text = "This meeting has no saved video. Its audio and transcript remain available."; }
        catch (Exception ex) { Status.Text = "Video list unavailable: " + ex.Message; }
    }
    internal async Task RecoverSelected()
    {
        if (Clips.SelectedItem is not SavedVideo video) { Status.Text = "Select a clip first."; return; }
        beforeMediaAccess?.Invoke();
        actions.IsEnabled = false; Clips.IsEnabled = false; Status.Text = "Recovering complete fragments…";
        try
        {
            var result = await main.StorageOperation(_ => VideoRecovery.Recover(directory, System.IO.Path.GetFileName(video.Path)).Path);
            if (result is null) { Status.Text = main.StorageMessage.Text; return; }
            Refresh(); Clips.SelectedItem = Clips.Items.Cast<SavedVideo>().FirstOrDefault(v => v.Path == result);
            Status.Text = "Recovered copy saved. Check playback; an incomplete tail may be missing. Original unchanged.";
        }
        finally { actions.IsEnabled = Clips.IsEnabled = true; }
    }
    private void Open()
    {
        try { beforeMediaAccess?.Invoke(); if (Clips.SelectedItem is SavedVideo video) Process.Start(new ProcessStartInfo(LocalBackup.Root(video.Path)) { UseShellExecute = true }); }
        catch (Exception) { Status.Text = "Video could not open. Try recovery or open the meeting folder."; }
    }
}

public partial class MainWindow
{
    private void MeetingVideo_Click(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is not MeetingRow row) { MeetingListStatus.Text = "Select a meeting first."; return; }
        new Window { Owner = this, Title = "Second Brain · Saved video", Style = (Style)FindResource("ShellWindowStyle"), Width = 720, Height = 460, MinWidth = 540, MinHeight = 360,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new VideoLibraryView(this, row.Path) }.Show();
    }
}
