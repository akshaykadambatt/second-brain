using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class MeetingOutcomeSmokeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var path = MeetingReviewSmokeTests.Fixture(directory); var before = File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"));
        var viewer = (await main.OpenTranscript(path))!;
        try
        {
            viewer.ReviewTabs.SelectedItem = viewer.OutcomesTab; viewer.UpdateLayout(); var view = viewer.Outcomes;
            check(view.Items.Items.Count == 3 && view.DraftText.Text.Length == 0, "Outcome review offers source-linked candidates and leaves follow-up empty until explicit review");
            view.Items.SelectedItem = view.Review!.Items.Single(i => i.Kind == OutcomeKind.Commitment); view.Choose(OutcomeChoice.Include); view.BuildDraft();
            check(view.DraftText.Text.Contains("Morgan will send the checklist on Friday.") && !view.DraftText.Text.Contains("Who will verify"), "Follow-up uses only explicitly included transcript wording without inventing owners or dates");
            viewer.GoToSegment("commitment");
            check(viewer.ReviewTabs.SelectedItem == viewer.TranscriptTab && (viewer.WordSelection.SelectedItem as ReviewWord)?.SegmentId == "commitment", "Outcome source navigation selects the exact original segment");
            viewer.ReviewTabs.SelectedItem = viewer.OutcomesTab; viewer.UpdateLayout(); capture(viewer, Path.Combine(directory, "meeting-outcomes.png"));
            viewer.Width = 800; viewer.Height = 660; viewer.UpdateLayout(); capture(viewer, Path.Combine(directory, "meeting-outcomes-compact.png"));
        }
        finally { viewer.Close(); }
        viewer = (await main.OpenTranscript(path))!;
        try
        {
            viewer.Outcomes.BuildDraft(); check(viewer.Outcomes.DraftText.Text.Contains("Morgan will send"), "Included outcome choices reopen in the same meeting");
            viewer.Outcomes.Items.SelectedItem = viewer.Outcomes.Review!.Items.Single(i => i.Kind == OutcomeKind.Commitment); viewer.Outcomes.Choose(OutcomeChoice.Pending); viewer.Outcomes.BuildDraft();
            check(viewer.Outcomes.DraftText.Text.Length == 0 && !main.Recorder.HasSession && before.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "transcript.jsonl"))), "Reset removes an item from drafts, preserves transcript bytes and never starts listening");
        }
        finally { viewer.Close(); }
        File.AppendAllText(Path.Combine(path, MeetingOutcomes.FileName), "corrupt\n"); viewer = (await main.OpenTranscript(path))!;
        check(viewer.Segments.Items.Count == 3 && viewer.Outcomes.Review is null && viewer.Outcomes.Status.Text.Contains("Could not"), "Damaged outcome journal cannot hide the original transcript"); viewer.Close();
    }
}
