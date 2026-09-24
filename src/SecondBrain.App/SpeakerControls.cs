using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private SpeakerSettings? speakerSettings;
    private bool speakerLoadFailed;
    private void InitializeSpeakers()
    {
        speakerSettings = new(dataDirectory); SpeakerOptions options;
        try { options = speakerSettings.Load(); }
        catch (Exception) { options = new(false); speakerLoadFailed = true; SpeakerSettingsStatus.Text = "Saved speaker settings are unreadable. Audio-only defaults apply until you explicitly save replacement settings."; }
        SpeakerSeparation.IsChecked = options.SeparateSpeakers; LocalParticipantName.Text = options.LocalParticipant; Transcriber.SpeakerOptions = options;
    }
    internal void ConfigureSpeakers(bool explicitSave = false)
    {
        if (speakerLoadFailed && !explicitSave) return;
        var options = new SpeakerOptions(SpeakerSeparation.IsChecked == true, LocalParticipantName.Text.Trim());
        speakerSettings!.Save(options); Transcriber.SpeakerOptions = options; speakerLoadFailed = false;
        SpeakerSettingsStatus.Text = "Saved for the next session. Remote labels are provisional and restart after reconnection.";
    }
    private void SpeakerSave_Click(object sender, RoutedEventArgs e)
    {
        try { ConfigureSpeakers(true); }
        catch (Exception) { SpeakerSettingsStatus.Text = "Speaker settings could not be saved. Check the name and folder access."; }
    }
}
