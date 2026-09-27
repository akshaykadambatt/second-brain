using System.IO;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class TranscriptWindow
{
    private void LoadSavedContent(MainWindow owner)
    {
        try
        {
            var archive = MeetingArchive.Read(directory);
            EvidenceText.Text = archive is null ? "This recording has no saved answer evidence. Its transcript and audio remain available."
                : "AI answer snapshots · verify claims against the quoted sources. General guidance is not a client fact.\n"
                    + (archive.Limited ? "Some material exceeded storage limits; excerpts are marked.\n" : "")
                    + (archive.Answers.Length == 0 ? "No answers were generated in this session." : string.Join("\n\n────────\n\n", archive.Answers.Select(a =>
                        $"{TimeSpan.FromSeconds(a.Seconds):hh\\:mm\\:ss} · {a.State}\n{a.Question}\n\n{a.Answer}\n\nSources available to this answer:\n"
                        + (a.Sources.Length == 0 ? "No saved source passages." : string.Join("\n\n", a.Sources.Select((s, i) => $"[S{i + 1}] {s.File}:{s.Line}\n{s.Text}"))))));
        }
        catch (Exception) { EvidenceText.Text = "Saved evidence is unavailable, damaged or no longer matches this meeting. Original transcript remains available."; }
        try
        {
            var manifest = RecordingSession.ReadManifest(directory);
            var context = SessionContextStore.ReadSnapshot(directory);
            if (context is not null && context.SessionId != manifest.Id) throw new InvalidDataException();
            var client = context?.Context.ProfileId ?? Guid.Empty;
            if (owner.Knowledge is null) throw new IOException();
            var path = VaultFiles.SafePath(owner.Knowledge.Root, $".secondbrain/receipts/{manifest.Id:N}.json");
            if (!File.Exists(path)) { ChangesText.Text = "No knowledge update receipt for this meeting in the current Vault. Manual notes remain in Knowledge."; return; }
            if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException();
            var receipt = JsonSerializer.Deserialize<MaintenanceReceipt>(File.ReadAllText(path));
            if (receipt is null || receipt.MeetingId != manifest.Id || receipt.Sections is not { Length: <= 100 }
                || receipt.Sections.Any(s => s is null || s.Added is not { Length: <= 20000 } || s.Path is null
                    || (client != Guid.Empty ? !s.Path.StartsWith($"Knowledge/Clients/{client:N}/", StringComparison.Ordinal) : s.Path.StartsWith("Knowledge/Clients/", StringComparison.Ordinal)))) throw new InvalidDataException();
            ChangesText.Text = "Knowledge update receipt · " + receipt.State + "\nRecorded additions may since have been edited or reverted. They are observations, not automatically confirmed facts.\n\n"
                + (receipt.Sections.Length == 0 ? "No supported additions were made." : string.Join("\n\n", receipt.Sections.Select(s => s.Path + "\n" + s.Added)));
        }
        catch (Exception) { ChangesText.Text = "Knowledge update history is unavailable for this recording and current Vault. Original transcript remains available."; }
    }
}
