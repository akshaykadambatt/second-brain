using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed class ReadableAnswerBuffer
{
    private string pending = "", held = "";
    private int total;
    public IReadOnlyList<string> Push(string delta, bool complete = false)
    {
        if ((total += delta.Length) > 20000) throw new InvalidDataException("Answer exceeded 20,000 characters.");
        pending += delta.Replace("\r", "", StringComparison.Ordinal);
        var blocks = new List<string>();
        int boundary;
        while ((boundary = pending.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
        {
            Add(pending[..boundary]); pending = pending[(boundary + 2)..];
        }
        if (complete) { Add(pending); pending = ""; if (held.Length > 0) throw new InvalidDataException("Answer ended with an unfinished sentence; it was withheld."); }
        if (pending.Length + held.Length > AnswerInbox.MaximumParagraphCharacters) throw new InvalidDataException("Answer paragraph is too long for comfortable reading.");
        return blocks;
        void Add(string text)
        {
            text = text.Trim(); if (text.Length == 0) return;
            held = held.Length == 0 ? text : held + " " + text;
            if (held.Length > AnswerInbox.MaximumParagraphCharacters) throw new InvalidDataException("Answer paragraph exceeded the reader limit.");
            if (Regex.IsMatch(held, "[.!?…][\"'”’)]*$")) { blocks.Add(held); held = ""; }
        }
    }
}
