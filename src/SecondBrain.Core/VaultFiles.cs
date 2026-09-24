using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public static class VaultFiles
{
    public static void Initialize(string root)
    {
        Directory.CreateDirectory(root);
        Create(root, "Home.md", "# Second Brain\n\nYour notes live here. Open this folder as a vault in Obsidian. The graph connects notes through their links.\n\n"
            + "- [[Context/Role]]\n- [[Context/Responsibilities]]\n- [[Context/Work domain]]\n- [[Context/Terminology]]\n- [[Projects/Projects]]\n- [[People/People]]\n- [[Meetings/Meetings]]\n\n"
            + "Use `project: Project name` and `date: YYYY-MM-DD` in Markdown frontmatter for search filters. Keep dated decisions instead of replacing history. Templates are excluded from search.\n");
        foreach (var (name, prompts) in new[] { ("Role", "Role title\n\nScope\n\nExperience"), ("Responsibilities", "Owned outcomes\n\nCurrent priorities\n\nBoundaries"),
            ("Work domain", "Business domain\n\nSystems\n\nConstraints"), ("Terminology", "Terms and definitions\n\nAcronyms\n\nPreferred wording") })
            Create(root, "Context/" + name + ".md", "# " + name + "\n\n[[Home]]\n\n<!-- Add your actual context below; no facts have been supplied yet. -->\n\n## " + prompts.Replace("\n\n", "\n\n## ") + "\n");
        foreach (var name in new[] { "Projects", "People", "Meetings" }) Create(root, name + "/" + name + ".md", "# " + name + "\n\n[[Home]]\n\nAdd links to relevant notes here. Meeting summaries also link back to this note automatically.\n");
        Create(root, "Templates/Project.md", "---\nproject: Project name\ndate: 2026-01-01\n---\n# Project name\n\n[[Projects/Projects]]\n\n## Purpose\n\n## Status and evidence\n\n## Dated decisions\n\n## People\n\n## Linked meetings\n");
        Create(root, "Templates/Person.md", "# Person name\n\n[[People/People]]\n\n## Role and responsibilities\n\n## Projects\n\n## Notes with dates and sources\n");
    }
    // Only creates absent files. A human edit is never overwritten by setup or import.
    public static bool Create(string root, string relative, string text)
    {
        var target = SafePath(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) return false;
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); try { File.Move(temp, target, false); return true; } catch (IOException) when (File.Exists(target)) { return false; } }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root); var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path is outside the vault.");
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Vault paths cannot traverse links or junctions.");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked vault files are not supported.");
        return path;
    }
    public static string ExportMeeting(string root, string recording, string project = "")
    {
        var manifest = RecordingSession.ReadManifest(recording);
        if (manifest.State is not ("Completed" or "Recovered" or "Failed")) throw new InvalidOperationException("Stop or recover the session before importing it.");
        var rawPath = Path.Combine(recording, "transcript.jsonl");
        if (new FileInfo(rawPath).Length > 40_000_000) throw new InvalidDataException("Transcript exceeds the 40 MB import limit.");
        var raw = File.ReadAllText(rawPath); var entries = TranscriptLog.Read(recording);
        if (entries.Any(e => e.SessionId != manifest.Id)) throw new InvalidDataException("Transcript session identity mismatch.");
        var date = manifest.StartedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var folder = $"Meetings/{date}-{manifest.Id:N}-{VaultIndex.Hash(raw)[..8]}";
        var prefix = $"---\ndate: {date}\nproject: \"{project.Replace("\"", "'").Replace("\n", " ").Replace("\r", " ")}\"\nsession_id: {manifest.Id}\nstarted_utc: {manifest.StartedUtc:O}\n---\n";
        var transcript = new StringBuilder(prefix + "# Meeting transcript\n\n[[" + folder + "/Summary]] · [[Meetings/Meetings]]\n\nFinalized machine transcription; gaps are explicit. Verify important facts against original audio.\n\n");
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; var time = TimeSpan.FromSeconds(entry.Start).ToString(@"hh\:mm\:ss\.fff");
            transcript.AppendLine($"## {time} · {entry.Source?.ToString() ?? "Session"} · {entry.Kind}\n");
            transcript.AppendLine(entry.Text + $"\n\n^t{i:D6}\n");
            if (entry.Source is { } source)
            {
                var audio = Path.Combine(recording, source == AudioSource.Microphone ? "microphone.wav" : "system.wav");
                var relative = Path.GetRelativePath(Path.Combine(root, folder), audio).Replace('\\', '/');
                transcript.AppendLine($"[Original audio]({EncodeRelative(relative)}#t={entry.Start.ToString("F3", CultureInfo.InvariantCulture)})\n");
            }
        }
        var excerpts = entries.Select((entry, index) => (entry, index)).Where(x => x.entry.Kind == "Final")
            .OrderByDescending(x => Regex.IsMatch(x.entry.Text, @"\b(decid|agree|action|next|deadline|will|owner)", RegexOptions.IgnoreCase)).Take(12).OrderBy(x => x.index).ToArray();
        var summary = new StringBuilder(prefix + "# Meeting summary\n\n[[Meetings/Meetings]] · [[" + folder + "/Transcript]]\n\n"
            + "Extractive summary: selected verbatim transcript excerpts, not AI conclusions. It may omit discussion; use the full transcript for decisions and context.\n\n");
        foreach (var (entry, index) in excerpts)
            summary.AppendLine($"- {entry.Start:F1}s · {entry.Source}: {entry.Text.Replace("\n", " ")} [[{folder}/Transcript#^t{index:D6}|source]]\n");
        if (excerpts.Length == 0) summary.AppendLine("No finalized speech was available. No decisions can be inferred.\n");
        if (entries.Any(e => e.Kind == "Gap")) summary.AppendLine("Transcription gaps are present; see the transcript.\n");
        Create(root, folder + "/transcript.jsonl", raw);
        Create(root, folder + "/Transcript.md", transcript.ToString());
        Create(root, folder + "/Summary.md", summary.ToString());
        return folder + "/Summary.md";
    }
    private static string EncodeRelative(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
