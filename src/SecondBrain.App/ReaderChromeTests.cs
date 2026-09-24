using System.IO;
using System.Windows;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ReaderChromeTests
{
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        main.Session.Load(ReaderSession.Sample);
        while (main.Panels.Count < 4) main.AddReader();
        var widths = new[] { 340d, 430, 620, 820 };
        for (var i = 0; i < 4; i++) { main.Panels[i].Width = widths[i]; main.Panels[i].Height = 430; }
        main.Session.Select(25);
        for (var i = 0; i < 3; i++) await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        foreach (var panel in main.Panels) panel.SetChromeVisibility(true);
        foreach (var panel in main.Panels) panel.UpdateLayout();
        var positions = main.Panels.Select(p => p.WordScreenY(25)).ToArray();
        var heights = main.Panels.Select(p => p.ReadingArea.ActualHeight).ToArray();
        capture(main.Panels[1], Path.Combine(directory, "controls-visible.png"));
        for (var cycle = 0; cycle < 6; cycle++)
        {
            foreach (var panel in main.Panels) panel.SetChromeVisibility(cycle % 2 != 0);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(main.Panels.Select((p, i) => Math.Abs(p.WordScreenY(25) - positions[i]) <= 1 && p.ReadingArea.ActualHeight == heights[i]).All(x => x),
                "Chrome visibility preserves the reading viewport and word position across four widths, cycle " + cycle);
        }
        foreach (var panel in main.Panels) panel.SetChromeVisibility(false);
        check(main.Panels.All(p => p.HeaderChrome.Opacity == 0 && !p.HeaderChrome.IsHitTestVisible && p.ResizeGrip.Opacity == 0),
            "Idle header and resize controls are visually quiet and do not intercept input");
        check(main.Panels.All(p => p.FooterChrome.Opacity == (p.CaptureExcluded ? 0 : 1)),
            "Idle footer hides normally but capture-exclusion failure remains visible");
        capture(main.Panels[1], Path.Combine(directory, "controls-hidden.png"));
        foreach (var panel in main.Panels) panel.SetChromeVisibility(true);
        check(main.Panels.All(p => p.HeaderChrome.IsHitTestVisible && p.FooterChrome.IsHitTestVisible && p.ResizeGrip.IsHitTestVisible),
            "Revealed chrome restores drag, controls and resize input");
        main.Session.Select(30);
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(main.Panels.All(p => p.SelectedWord == 30 && NativeWindows.IsToolWindow(p) && !p.ShowInTaskbar),
            "Manual word navigation and ghost window styles survive chrome changes");
    }
}
