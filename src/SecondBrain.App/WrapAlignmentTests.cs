using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class WrapAlignmentTests
{
    public static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        const string text = "In practice, that can include reducing emissions, treating workers fairly, and supporting local communities. In a technical conversation, though, CSR can also mean a certificate signing request. These additional words keep the next passage visible for this reading check.";
        while (main.Panels.Count < 4) main.AddReader();
        var widths = new[] { 340d, 380, 460, 700 };
        for (var i = 0; i < 4; i++) { main.Panels[i].Width = widths[i]; main.Panels[i].Height = 650; }
        main.Session.SetStyle(main.Session.Style with { FontSize = 42, ColumnCharacters = 48 });
        async Task Settle() { for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        var inbox = new AnswerInbox(); var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Wrapped answer");
        inbox.Accept(new(answer.RequestId, answer.Id, 0, text + "\n\n")); main.Session.ShowAnswer(answer); await Settle();
        var maxGlyphError = 0d;
        foreach (var streamed in new[] { false, true })
        {
            if (streamed) main.Session.ShowAnswer(answer); else main.Session.Load(text);
            await Settle();
            for (var word = 0; word < main.Session.Words.Count; word++)
            {
                main.Session.Select(word); await Settle();
                maxGlyphError = Math.Max(maxGlyphError, main.Panels.Max(p => p.MarkerGlyphError));
            }
        }
        check(maxGlyphError <= 1, "Marker centers on actual glyph line for every word, including wrap boundaries, in scripts and streamed answers");
        main.Session.Select(0); await Settle();
        main.Playback.StartVoice();
        var before = main.Panels.Select(p => p.ScrollPosition).ToArray();
        var samples = new List<double[]>();
        void Frame(object? sender, EventArgs args) => samples.Add(main.Panels.Select(p => p.ScrollPosition).ToArray());
        CompositionTarget.Rendering += Frame;
        try
        {
            check(main.Playback.Voice.Observe(new(0, 2, string.Join(" ", main.Session.Words.Take(12).Select(w => w.Text)), true, true, .99f)), "Delayed multi-line phrase is matched through the real voice controller");
            await Task.Delay(6500);
            var errors = main.Panels.Select(p => p.AnchorError).ToArray();
            var travel = main.Panels.Select((p, i) => (p.ScrollPosition - before[i]) / p.LineHeight).ToArray();
            File.WriteAllText(Path.Combine(directory, "wrap-measurements.json"), JsonSerializer.Serialize(new { widths, selectedWord = main.Session.Position, errors, travel, maxGlyphError }, new JsonSerializerOptions { WriteIndented = true }));
            capture(main.Panels[0], Path.Combine(directory, "narrow-reader.png"));
            check(main.Panels.All(p => p.AnchorError <= 1), "All widths settle the selected wrapped word at the band within one DIP after a sparse speech burst");
            check(main.Panels.All(p => p.MarkerGlyphError <= 1), "Marker centers on the accepted word's real glyph line after voice catch-up");
            check(travel[0] > 2, "Narrow panel can finish confirmed progress spanning multiple visual lines");
            var bounded = true;
            for (var n = 1; n < samples.Count; n++) for (var p = 0; p < 4; p++)
                bounded &= samples[n][p] - samples[n - 1][p] is >= -.01 && samples[n][p] - samples[n - 1][p] <= main.Panels[p].LineHeight * 2.2 / 30 + .05;
            check(bounded, "Confirmed catch-up is monotonic and bounded per frame");
            var held = main.Panels.Select(p => p.ScrollPosition).ToArray(); await Task.Delay(400);
            check(main.Panels.Select((p, i) => Math.Abs(p.ScrollPosition - held[i]) < .01).All(x => x), "Silence holds exactly at the confirmed line after settling");
            inbox.Accept(new(answer.RequestId, answer.Id, 1, "An additional paragraph must leave the selected line in place.\n\n")); main.Session.RefreshAnswer(answer); await Settle();
            check(main.Panels.All(p => p.MarkerGlyphError <= 1), "Appending a new block cannot displace the accepted wrapped line");
            main.Panels[0].Width = 410; main.Session.SetStyle(main.Session.Style with { FontSize = 36 }); await Settle();
            check(main.Panels.All(p => p.SelectedWord == 12 && p.MarkerGlyphError <= 1), "Resizing and font changes rebuild each panel's actual word-line mapping under voice following");
            main.Playback.StopVoice();
        }
        finally { CompositionTarget.Rendering -= Frame; main.Playback.StopVoice(); }
    }
}
