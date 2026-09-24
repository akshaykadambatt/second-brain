using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ShellSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        async Task Settle() { await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        var document = main.Session.DocumentId; var word = main.Session.Position;
        main.LiveTab.IsSelected = true; await Settle();
        check(main.ListenButton.IsVisible && !main.MicrophonePicker.IsVisible, "Live has its start action without persistent device controls");
        check(main.SessionStateText.Text == "READY" && !main.LiveDetails.IsExpanded && !main.LiveEvidence.IsExpanded,
            "Idle Live state is clear and transcript/evidence remain collapsed by default");
        capture(main, Path.Combine(directory, "live.png"));
        main.SettingsTab.IsSelected = true; main.DevicesTab.IsSelected = true; await Settle();
        check(main.MicrophonePicker.IsVisible && main.LiveOutputPicker.IsVisible, "Audio devices remain available in Settings");
        capture(main, Path.Combine(directory, "settings.png"));
        main.PracticeTab.IsSelected = true; await Settle();
        check(main.ScriptEditor.IsVisible && main.PracticeListenButton.IsVisible, "Practice and diagnostics remain reachable");
        main.StorageTab.IsSelected = true; await Settle();
        check(main.StorageRecordings.IsVisible, "Backup, restore and recording recovery remain reachable");
        main.KnowledgeTab.IsSelected = true; await Settle();
        check(main.VaultQuery.IsVisible, "Knowledge retains the existing source search");
        main.MeetingsTab.IsSelected = true; main.HistoryTab.IsSelected = true; await Settle();
        check(main.HistoryRevisions.IsVisible, "Private note history remains accessible under Meetings");
        ((TabControl)main.HistoryTab.Parent).SelectedIndex = 0;
        await main.RefreshMeetings();
        using (var fixture = new RecordingSession(Path.Combine(directory, "recordings"), [new(AudioSource.Microphone, "fixture", "Synthetic microphone", 16000), new(AudioSource.System, "output", "Synthetic output", 16000)]))
        { fixture.Write(new(AudioSource.Microphone, 0, new byte[320])); fixture.Complete(.01); }
        await main.RefreshMeetings(); await Settle();
        check(main.MeetingList.Items.Count > 0 && main.MeetingList.SelectedItem is not null, "Meetings lists a saved session without opening recording controls");
        capture(main, Path.Combine(directory, "meetings.png"));
        main.NavigationToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        check(System.Windows.Automation.AutomationProperties.GetName(main.KnowledgeTab) == "Knowledge", "Compact navigation retains accessible page names");
        main.NavigationToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.LiveTab.IsSelected = true; await Settle();
        check(!main.Recorder.HasSession && !main.Voice.Running && main.Companion?.Active != true, "Browsing and compacting navigation never starts capture");
        check(main.Session.DocumentId == document && main.Session.Position == word, "Page navigation preserves the reader document and position");
        var selectedMic = main.MicrophonePicker.SelectedItem;
        main.MicrophonePicker.SelectedItem = null;
        check(!await main.StartCompanion() && main.SessionStateText.Text == "ATTENTION" && !main.Recorder.HasSession,
            "Missing audio selection reports attention without starting capture");
        main.MicrophonePicker.SelectedItem = selectedMic;
    }
}
