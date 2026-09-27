using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class MeetingVideoView : UserControl
{
    internal VideoLibraryView Library { get; }
    internal MediaElement? Player { get; private set; }
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    internal double? RequestedOffset { get; private set; }
    internal bool Opened { get; private set; }
    private readonly Border display = new() { Background = Brushes.Black, MinHeight = 120, CornerRadius = new CornerRadius(12) };
    private readonly DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Action beforePlay;
    private readonly bool muted;
    internal MeetingVideoView(MainWindow owner, string directory, Action beforePlay, bool muted)
    {
        this.beforePlay = beforePlay; this.muted = muted;
        Library = new(owner, directory, Stop) { MaxHeight = 260 };
        var grid = new Grid { Margin = new Thickness(8) };
        foreach (var size in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new() { Height = size });
        grid.Children.Add(Library); Grid.SetRow(display, 1); grid.Children.Add(display);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        Button Add(string name) { var b = new Button { Content = name, Margin = new Thickness(0, 0, 8, 0) }; buttons.Children.Add(b); return b; }
        Add("Play selected video").Click += (_, _) => { if (Library.Clips.SelectedItem is SavedVideo clip) Play(clip, 0); };
        Add("Pause / resume").Click += (_, _) => { if (Opened && Player is { } player) { if (paused) player.Play(); else player.Pause(); paused = !paused; } };
        Add("Stop video").Click += (_, _) => Stop();
        Grid.SetRow(Status, 3); grid.Children.Add(Status); Content = grid;
        Status.Text = "Select a saved clip, or use Video at timestamp from the transcript. Video audio plays on its own; no second audio track is started.";
        Library.Clips.SelectionChanged += (_, _) => Stop();
        timeout.Tick += (_, _) => { Stop(); Status.Text = "Playback did not open. Use Open video for an external player or recover a copy."; };
        Unloaded += (_, _) => Stop();
    }
    private bool paused;
    internal bool PlayAt(double seconds)
    {
        var candidates = Library.Clips.Items.Cast<SavedVideo>().Where(c => c.Start is { } start && c.Duration is { } duration && seconds >= start && seconds < start + duration).ToArray();
        // A recovered copy may overlap its original. Prefer the selected covering clip; otherwise refuse ambiguity.
        var clip = Library.Clips.SelectedItem is SavedVideo selected && candidates.Contains(selected) ? selected : candidates.Length == 1 ? candidates[0] : null;
        if (clip is null) { Stop(); Status.Text = "No unique clip with verified timing covers this timestamp. Select a clip to play manually; recovered duration may be unknown."; return false; }
        Library.Clips.SelectedItem = clip; Play(clip, seconds - clip.Start!.Value); return true;
    }
    internal void Play(SavedVideo clip, double offset)
    {
        Stop(); beforePlay();
        try
        {
            if (!double.IsFinite(offset) || offset < 0) throw new InvalidDataException();
            var source = LocalBackup.Root(clip.Path);
            if (!File.Exists(source)) throw new FileNotFoundException();
            var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, IsMuted = true };
            Player = player; display.Child = player; RequestedOffset = offset; paused = false;
            player.MediaOpened += (_, _) =>
            {
                if (Player != player) return;
                timeout.Stop();
                if (!player.NaturalDuration.HasTimeSpan || offset >= player.NaturalDuration.TimeSpan.TotalSeconds)
                { Stop(); Status.Text = "Saved video does not contain that timestamp."; return; }
                player.Play();
                // Let WPF apply its play transition before seeking; that transition can reset a pending position.
                Dispatcher.BeginInvoke(() =>
                {
                    if (Player != player) return;
                    player.Position = TimeSpan.FromSeconds(offset); player.IsMuted = muted; Opened = true;
                    Status.Text = $"Playing clip from {TimeSpan.FromSeconds(offset):hh\\:mm\\:ss} · " + (clip.Audio ? "includes saved meeting audio" : "no saved audio in this clip");
                }, DispatcherPriority.Background);
            };
            player.MediaFailed += (_, _) => { if (Player == player) { Stop(); Status.Text = "Windows playback could not open this clip. Use Open video or recover a copy; original media is unchanged." + (source.Length >= 260 ? " This path is long; try a shorter location for the restored app and data." : ""); } };
            player.MediaEnded += (_, _) => { if (Player == player) { Stop(); Status.Text = "Video ended."; } };
            Status.Text = "Opening saved video…"; player.Source = new Uri(source); player.Pause(); timeout.Start();
        }
        catch (Exception) { Stop(); Status.Text = "Saved video is unavailable. Refresh the list or open the meeting folder."; }
    }
    internal void Stop()
    {
        timeout.Stop(); var old = Player; Player = null; Opened = false; RequestedOffset = null;
        old?.Close(); display.Child = null;
    }
}
