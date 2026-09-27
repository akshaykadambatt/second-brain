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
        check(Grid.GetColumn(main.SessionWorkspace) == 1 && main.LiveAnswer.ActualWidth >= 400,
            "Wide Live layout gives the answer a readable column beside session controls");
        var originalWidth = main.Width; var originalHeight = main.Height;
        var liveView = (ScrollViewer)main.LiveTab.Content;
        foreach (var width in new[] { 1000d, 620d })
        {
            main.Width = width; main.Height = 700; await Settle(); liveView.ScrollToTop(); await Settle();
            check(Grid.GetRow(main.SessionWorkspace) == 1 && liveView.ScrollableWidth == 0 && main.WorkspaceScroll.ScrollableWidth == 0,
                $"At {width} DIP, Live stacks session controls without horizontal scrolling");
            check(main.LiveAnswer.ActualWidth >= 280 && main.ListenButton.TranslatePoint(new Point(0, main.ListenButton.ActualHeight), liveView).Y <= liveView.ViewportHeight,
                $"At {width} DIP, the start action stays visible and answer text remains readable");
            main.SessionWorkspace.BringIntoView(); await Settle();
            check(liveView.VerticalOffset > 0, $"At {width} DIP, session controls remain reachable through scrolling");
            liveView.ScrollToTop(); await Settle(); capture(main, Path.Combine(directory, $"live-{width}.png"));
        }
        main.Width = originalWidth; main.Height = originalHeight; await Settle();
        main.ClientPicker.IsDropDownOpen = true; await Settle();
        check(main.ClientPicker.Template.FindName("PART_Popup", main.ClientPicker) is System.Windows.Controls.Primitives.Popup { IsOpen: true },
            "Restyled client selector retains its working dropdown");
        main.ClientPicker.IsDropDownOpen = false;
        main.SettingsTab.IsSelected = true; main.DevicesTab.IsSelected = true; await Settle();
        check(main.MicrophonePicker.IsVisible && main.LiveOutputPicker.IsVisible, "Audio devices remain available in Settings");
        capture(main, Path.Combine(directory, "settings.png"));
        main.ReaderSettingsTab.IsSelected = true; await Settle();
        check(main.FontSlider.IsVisible && main.WidthSlider.IsVisible && !main.MicrophonePicker.IsVisible,
            "Reader appearance has a dedicated settings page separate from audio setup");
        capture(main, Path.Combine(directory, "reader-settings.png"));
        main.PracticeTab.IsSelected = true; await Settle();
        check(main.ScriptEditor.IsVisible && main.PracticeListenButton.IsVisible, "Practice and diagnostics remain reachable");
        main.StorageTab.IsSelected = true; await Settle();
        check(main.StorageRecordings.IsVisible, "Backup, restore and recording recovery remain reachable");
        main.KnowledgeTab.IsSelected = true; await Settle();
        check(main.VaultQuery.IsVisible, "Knowledge retains the existing source search");
        var knowledgeView = (ScrollViewer)main.KnowledgeTab.Content;
        check(!main.DocumentImportsPanel.IsExpanded && !main.VaultSettingsPanel.IsExpanded &&
            main.VaultQuery.TranslatePoint(new Point(0, main.VaultQuery.ActualHeight), knowledgeView).Y < knowledgeView.ViewportHeight,
            "Knowledge search appears above the fold without import or vault-configuration clutter");
        capture(main, Path.Combine(directory, "knowledge.png"));
        main.DocumentImportsPanel.IsExpanded = true; main.VaultSettingsPanel.IsExpanded = true; await Settle();
        check(main.DocumentChoose.IsVisible && main.VaultApply.IsVisible, "Import and filter controls remain accessible through their disclosures");
        main.DocumentImportsPanel.IsExpanded = false; main.VaultSettingsPanel.IsExpanded = false;
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
