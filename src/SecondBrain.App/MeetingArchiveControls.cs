using System.IO;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private async Task SaveMeetingArchive(string directory, CompanionSession companion)
    {
        try
        {
            var limited = companion.Answers.Requests.Count > 256;
            string Bound(string value, int length) { if (value.Length <= length) return value; limited = true; return value[..(length - 16)] + " [excerpt ends]"; }
            var answers = companion.Answers.Requests.Take(256).Select(r =>
            {
                var hits = r.Knowledge?.Hits ?? [];
                if (hits.Count > 12) limited = true;
                return new ArchivedAnswer(r.Id, Math.Clamp(r.CreatedAt - Recorder.ClockOrigin, 0, 86400), Bound(r.Question, 2000),
                    Bound(string.Join("\n\n", r.Fast.Blocks.Select(b => b.Text).Concat(r.Deeper?.Blocks.Select(b => b.Text) ?? [])), 20000),
                    Bound(r.Status, 200), hits.Take(12).Select(h => new ArchivedSource(h.Chunk.ClientId, h.Chunk.File, h.Chunk.Line, Bound(h.Chunk.Text, 2000))).ToArray());
            }).ToArray();
            await Task.Run(() => MeetingArchive.Save(directory, answers, limited));
        }
        catch (Exception ex)
        { CompanionStatus.Text += " Answer evidence could not be archived; audio and transcript remain saved."; log.Write("Meeting evidence save failure=" + ex.GetType().Name); }
    }
}
