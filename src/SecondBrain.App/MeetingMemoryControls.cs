using System.IO;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    private async Task SaveMeetingMemory(string directory)
    {
        try { await Task.Run(() => MeetingMemory.Save(directory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { CompanionStatus.Text += " Earlier-context summary could not be saved; the original transcript is retained."; log.Write("Memory sidecar failure=" + ex.GetType().Name); }
    }
    private void RefreshMeetingMemory()
    {
        if (MeetingMemoryDetails.IsExpanded)
            MeetingMemoryText.Text = AssistantContext.Memory.Excerpts.Count == 0 ? "The recent transcript still contains the meeting context. Older source excerpts will appear here as it grows."
                : AssistantContext.Memory.Snapshot();
    }
}
