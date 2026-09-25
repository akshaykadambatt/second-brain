using System.IO;
using System.Windows;
using System.Windows.Controls;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private bool refinementBusy;
    private async void Refine_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string value } && Enum.TryParse<AnswerRefinement>(value, out var mode)) await RefineAnswer(mode); }
    internal Task<AnswerRequest?> RefineAnswer(AnswerRefinement mode, IAnswerProvider? testProvider = null)
    {
        var task = Work(); assistantShutdowns.RemoveAll(t => t.IsCompleted); assistantShutdowns.Add(task); return task;
        async Task<AnswerRequest?> Work()
        {
            if (closing || refinementBusy || Companion is not { Selected: { WordCount: > 0 } selected } companion) return null;
            var source = companion.Answers.Requests.FirstOrDefault(r => r.Id == selected.RequestId); if (source is null) return null;
            refinementBusy = true; RefinementButtons.IsEnabled = false; RefinementStatus.Text = "Preparing a separate " + mode.ToString().ToLowerInvariant() + " variant…";
            using var owned = testProvider is null ? new OpenAiAnswerProvider(new ApiKeyStore(dataDirectory, "OpenAI").Load) : null;
            try
            {
                var latest = companion.Answers.LatestPrimary?.Id;
                var run = companion.Answers.Refine(source, selected, mode, testProvider ?? owned!); await run.Work;
                if (!closing && Companion == companion && companion.Selected == selected && companion.Answers.LatestPrimary?.Id == latest && run.Fast.State == AnswerState.Complete && run.Fast.WordCount > 0)
                { companion.Select(run.Fast); RefreshCompanion(); }
                if (!closing) RefinementStatus.Text = run.Fast.State == AnswerState.Complete ? "Variant saved in answer history. Original answer returns to its source." : run.Status;
                return run;
            }
            catch (Exception ex) { if (!closing) RefinementStatus.Text = "Could not refine: " + ex.Message; return null; }
            finally { refinementBusy = false; RefinementButtons.IsEnabled = !closing; }
        }
    }
    private void SaveTalkTrack_Click(object sender, RoutedEventArgs e)
    {
        if (Knowledge is not { } knowledge || Companion is not { Selected: { WordCount: > 0 } answer } companion) return;
        try
        {
            var run = companion.Answers.Requests.First(r => r.Id == answer.RequestId);
            var context = Recorder.LastDirectory is { } folder ? SessionContextStore.ReadSnapshot(folder) : null;
            if (context is null || context.SessionId != run.SessionId || context.Context.ProfileId == Guid.Empty) throw new InvalidDataException("Talk tracks need a named client meeting and supporting source. You can also create one in Client knowledge.");
            var source = run.Knowledge?.Hits.FirstOrDefault()?.Chunk ?? throw new InvalidDataException("No supporting source is attached to this answer. Add source material before saving reusable guidance.");
            var window = new ClientKnowledgeWindow(knowledge.Root, contextBook.Profiles, context.Context.ProfileId, SaveClientKnowledge, relative => OpenVaultNote(knowledge.Root, relative, true)) { Owner = this };
            window.Draft(new() { ClientId = context.Context.ProfileId, Kind = KnowledgeKind.TalkTrack, Name = run.Question[..Math.Min(160, run.Question.Length)], Project = context.Context.Project,
                Text = string.Join("\n\n", answer.Blocks.Select(b => b.Text)), Source = source.File, SourceLine = source.Line, Quote = source.Text, Date = source.Date });
            window.ShowDialog();
        }
        catch (Exception ex) { RefinementStatus.Text = "Talk track not saved: " + ex.Message; }
    }
    private void OriginalAnswer_Click(object sender, RoutedEventArgs e) => ShowOriginalAnswer();
    internal void ShowOriginalAnswer()
    {
        if (Companion is { Selected.ParentAnswerId: { } parent } companion && companion.Answers.Inbox.Answers.FirstOrDefault(a => a.Id == parent) is { } source)
        { companion.Select(source); RefreshCompanion(); }
    }
}
