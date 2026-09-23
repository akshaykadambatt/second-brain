using System.Text.RegularExpressions;

namespace SecondBrain.Core;

// Recognition-independent, conservative matching. Repeated hypotheses for one utterance
// are compared against its fixed starting anchor, preventing duplicate text from advancing twice.
public sealed class SpeechFollower(ReaderSession session)
{
    private int anchor;
    private int previousCandidate = -1;
    public void BeginUtterance() { anchor = session.Position; previousCandidate = -1; }
    public static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "");

    public bool Observe(string heard, bool final, float confidence)
    {
        if (final && confidence < .30f) return false;
        var speech = heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(x => x.Length > 0 && x is not "um" and not "uh").ToArray();
        if (speech.Length == 0 || session.Position >= session.Words.Count) return false;
        var script = session.Words.Select(w => Normalize(w.Text)).ToArray();
        var bestEnd = -1;
        var bestScore = double.NegativeInfinity;
        for (var start = Math.Max(0, anchor - 4); start < Math.Min(script.Length, anchor + 24); start++)
        {
            int pos = start, matched = 0, missed = 0, skipped = 0, end = start;
            foreach (var word in speech)
            {
                var found = -1;
                for (var k = pos; k < Math.Min(script.Length, pos + 3); k++)
                    if (script[k] == word) { found = k; break; }
                if (found < 0) { missed++; continue; }
                skipped += found - pos;
                pos = found + 1;
                end = pos;
                matched++;
            }
            var minimum = speech.Length == 1 && final && confidence >= .75f && start == anchor ? 1 : 2;
            if (matched < minimum || matched < speech.Length * .7 || skipped > matched) continue;
            var score = matched * 4 - missed * 2 - skipped - Math.Abs(start - anchor) * .2;
            if (score > bestScore) { bestScore = score; bestEnd = end; }
        }
        if (bestEnd < 0) { previousCandidate = -1; return false; }
        var accepted = final ? bestEnd : previousCandidate < 0 ? -1 : Math.Min(previousCandidate, bestEnd);
        previousCandidate = bestEnd;
        if (accepted <= session.Position) return false;
        session.Select(accepted, fromVoice: true);
        return true;
    }
}
