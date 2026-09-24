using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record SpeechMatch(int Start, int End, int Matched, bool Stable, bool Final);

// Recognition-independent, conservative matching. Repeated hypotheses for one utterance
// are compared against its fixed starting anchor, preventing duplicate text from advancing twice.
public sealed class SpeechFollower(ReaderSession session)
{
    private int anchor;
    private int previousCandidate = -1;
    private Guid cachedDocument;
    private readonly List<string> script = [];
    public SpeechMatch? LastMatch { get; private set; }
    public string Reason { get; private set; } = "Waiting for a nearby phrase";
    public void BeginUtterance() { anchor = session.Position; previousCandidate = -1; LastMatch = null; }
    public static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "");

    public bool Observe(string heard, bool final, float confidence)
    {
        LastMatch = null; Reason = "Uncertain speech · holding position";
        if (!float.IsFinite(confidence) || confidence < (final ? .45f : .65f)) { previousCandidate = -1; return false; }
        var speech = heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(x => x.Length > 0 && x is not "um" and not "uh").ToArray();
        if (speech.Length == 0 || session.Position >= session.Words.Count) return false;
        if (cachedDocument != session.DocumentId || script.Count > session.Words.Count) { script.Clear(); cachedDocument = session.DocumentId; }
        for (var i = script.Count; i < session.Words.Count; i++) script.Add(Normalize(session.Words[i].Text));
        var bestEnd = -1;
        var bestScore = double.NegativeInfinity;
        var candidates = new List<(int Start, int End, int Matched, double Quality, double Score)>();
        for (var start = Math.Max(0, anchor - 4); start < Math.Min(script.Count, anchor + 13); start++)
        {
            int pos = start, matched = 0, missed = 0, skipped = 0, end = start;
            foreach (var word in speech)
            {
                var found = -1;
                for (var k = pos; k < Math.Min(script.Count, pos + 3); k++)
                    if (script[k] == word) { found = k; break; }
                if (found < 0) { missed++; continue; }
                skipped += found - pos;
                pos = found + 1;
                end = pos;
                matched++;
            }
            var minimum = speech.Length == 1 && final && confidence >= .75f && start == anchor ? 1 : 2;
            if (matched < minimum || matched < speech.Length * .7 || skipped > matched) continue;
            var quality = matched * 4 - missed * 2 - skipped;
            var score = quality - Math.Abs(start - anchor) * .4;
            candidates.Add((start, end, matched, quality, score));
            if (score > bestScore) { bestScore = score; bestEnd = end; }
        }
        if (bestEnd < 0) { previousCandidate = -1; return false; }
        var best = candidates.OrderByDescending(c => c.Score).First();
        // An exact phrase at the anchor is safe even if it occurs later too.
        // A jump ahead needs a distinctive phrase, not merely the nearest repeat.
        if (best.Start > anchor + 1 && (best.Matched < 3 || candidates.Any(c => c.End != best.End && c.Quality >= best.Quality - 1)))
        { previousCandidate = -1; Reason = "Repeated or short phrase · select a word to recover"; return false; }
        var accepted = final ? bestEnd : previousCandidate < 0 ? -1 : Math.Min(previousCandidate, bestEnd);
        previousCandidate = bestEnd;
        // Repeated provisional agreement may confirm a small prefix, never a
        // distant leap. Finals can confirm the full distinctive local match.
        if (!final && accepted > session.Position) accepted = Math.Min(accepted, session.Position + 3);
        LastMatch = new(best.Start, best.End, best.Matched, accepted >= 0, final);
        Reason = accepted > session.Position ? "Following nearby words" : "Waiting for stable speech";
        if (accepted <= session.Position) return false;
        session.Select(accepted, fromVoice: true);
        return true;
    }
}
