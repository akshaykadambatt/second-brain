using System.IO;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class DateFormatSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var began = new DateTimeOffset(2026, 9, 27, 15, 10, 0, TimeSpan.Zero);
        using (var session = new RecordingSession(Path.Combine(directory, "recordings"), [new(AudioSource.Microphone, "fixture", "Synthetic", 16000), new(AudioSource.System, "fixture", "Synthetic", 48000)], began)) session.Complete(90);
        await main.RefreshMeetings(); var row = main.MeetingList.Items[0];
        var label = row.GetType().GetProperty("Label")!.GetValue(row) as string;
        check(label is not null && label.Contains(DisplayFormats.LocalDateTime(began)) && label.Contains("00:01:30"), "Meeting list uses word-month dates and local 12-hour time while preserving elapsed duration");
        var root = main.Knowledge!.Root; main.VaultFrom.Text = "2-January-2026"; main.VaultUntil.Text = "2026-09-27";
        main.VaultApply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(main.VaultFrom.Text == "02-January-2026" && main.VaultUntil.Text == "27-September-2026", "Date filters accept display and legacy input and normalize both visibly");
        check(main.KnowledgeFilterSummary.Text.Contains("02-January-2026") && main.KnowledgeFilterSummary.Text.Contains("27-September-2026"),
            "Applied date filters remain visible in the search summary when configuration is collapsed");
        var client = new SessionContext { ProfileId = Guid.NewGuid(), Client = "Cedar" };
        File.WriteAllText(Path.Combine(root, "Source.md"), "Taylor will send the report Friday.");
        var editor = new ClientKnowledgeWindow(root, [client], client.ProfileId, main.SaveClientKnowledge, _ => { }) { Owner = main }; editor.Show();
        try
        {
            editor.Kind.SelectedItem = KnowledgeKind.Commitment; editor.NameField.Text = "Report"; editor.Body.Text = "Track the sourced report.";
            editor.Source.Text = "Source.md"; editor.Quote.Text = "Taylor will send the report Friday."; editor.Date.Text = "27-September-2026";
            await editor.Save(false);
            var saved = new ClientKnowledgeStore(root).List(client.ProfileId).Documents.Single();
            check(saved.Value.Date == new DateOnly(2026, 9, 27) && editor.Date.Text == "27-September-2026", "Client date entry round-trips in the requested format");
            check(File.ReadAllText(VaultFiles.SafePath(root, saved.Value.Relative)).Contains("2026-09-27"), "Machine-readable frontmatter stays backward compatible");
        }
        finally { editor.Close(); }
        main.MeetingsTab.IsSelected = true; capture(main, Path.Combine(directory, "meeting-date-time.png"));
    }
}
