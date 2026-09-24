using System.Text.RegularExpressions;

namespace SecondBrain.Core;

public sealed record ReaderStyle(double FontSize = 32, double ColumnCharacters = 36,
    double LineSpacing = 1.4, double BackgroundOpacity = 0.94, double ReadingBand = 0.33)
{
    public bool IsValid => FontSize is >= 18 and <= 64 && ColumnCharacters is >= 20 and <= 72
        && LineSpacing is >= 1.1 and <= 2.2 && BackgroundOpacity is >= .25 and <= 1
        && ReadingBand is >= .15 and <= .7;
}

// Physical desktop pixels, independent of the DPI of the control window.
public sealed record PanelPlacement(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Width is >= 200 and <= 16000 && Height is >= 150 and <= 16000;
    public PanelPlacement FitTo(PanelPlacement workArea) => new(
        Math.Clamp(Left, workArea.Left, workArea.Left + workArea.Width - Math.Min(Width, workArea.Width)),
        Math.Clamp(Top, workArea.Top, workArea.Top + workArea.Height - Math.Min(Height, workArea.Height)),
        Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
}

public enum ReaderChange { Document, Appearance, Position, VoicePosition, TimedPosition, Append, StreamState }
public sealed record ScriptWord(int Id, string Text, string Suffix, Guid BlockId = default);

public sealed class ReaderSession
{
    public const string Sample = "Today I want to talk about our next steps. We are starting with a simple reader that follows my voice. "
        + "As I read, the words I have finished will fade and the next word will be highlighted.\n\n"
        + "I can pause here and take a breath. The text should wait for me. When I start speaking again, it should continue from the same place. "
        + "If I lose my place, I can click a word to return to it.\n\n"
        + "The goal is to read comfortably while keeping my eyes close to the camera. I can adjust the font size, the width of the text, and the space between lines. "
        + "Every floating panel should show the same reading position, even when the windows are different sizes.";

    public string Text { get; private set; } = "";
    public Guid DocumentId { get; private set; }
    public IReadOnlyList<ScriptWord> Words { get; private set; } = [];
    public int Position { get; private set; }
    public ReaderStyle Style { get; private set; } = new();
    public StreamAnswer? Answer { get; private set; }
    public bool AwaitingText => Answer?.State == AnswerState.Receiving;
    public event Action<ReaderChange>? Changed;

    public void Load(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a sentence before opening the reader.");
        if (text.Length > 20000) throw new ArgumentException("Please keep this prototype's script under 20,000 characters.");
        Text = text.Trim();
        Answer = null;
        DocumentId = Guid.NewGuid();
        var matches = Regex.Matches(Text, @"\S+");
        Words = matches.Select((m, i) => new ScriptWord(i, m.Value,
            i + 1 < matches.Count ? Text[(m.Index + m.Length)..matches[i + 1].Index] : "")).ToArray();
        Position = 0;
        Changed?.Invoke(ReaderChange.Document);
    }

    public void ShowAnswer(StreamAnswer answer)
    {
        Answer = answer; DocumentId = answer.Id;
        Words = answer.Blocks.SelectMany(b => b.Words).ToArray();
        Text = string.Join("\n\n", answer.Blocks.Select(b => b.Text));
        Position = Math.Clamp(answer.SavedPosition, 0, Words.Count);
        Changed?.Invoke(ReaderChange.Document);
    }
    public void RefreshAnswer(StreamAnswer answer)
    {
        if (Answer != answer) return;
        var append = answer.WordCount > Words.Count;
        if (append)
        {
            Words = answer.Blocks.SelectMany(b => b.Words).ToArray();
            Text = string.Join("\n\n", answer.Blocks.Select(b => b.Text));
        }
        Changed?.Invoke(append ? ReaderChange.Append : ReaderChange.StreamState);
    }

    public void SetStyle(ReaderStyle style)
    {
        if (!style.IsValid) throw new ArgumentException("Invalid reader appearance.");
        Style = style;
        Changed?.Invoke(ReaderChange.Appearance);
    }

    public void Select(int word, bool fromVoice = false)
    {
        var next = Math.Clamp(word, 0, Words.Count);
        if (next == Position && fromVoice) return;
        Position = next;
        Changed?.Invoke(fromVoice ? ReaderChange.VoicePosition : ReaderChange.Position);
    }
    public void SelectTimed(int word)
    {
        var next = Math.Clamp(word, 0, Words.Count);
        if (next == Position) return;
        Position = next; Changed?.Invoke(ReaderChange.TimedPosition);
    }
}
