using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class ShortcutTests
{
    internal sealed class Platform : IShortcutPlatform
    {
        internal readonly Dictionary<int, (uint Modifiers, uint Key)> Registered = [];
        internal uint? BlockedKey;
        public bool Register(IntPtr window, int id, uint modifiers, uint key)
        { if (BlockedKey == key) return false; Registered.Add(id, (modifiers, key)); return true; }
        public void Unregister(IntPtr window, int id) => Registered.Remove(id);
    }
    internal static async Task Run(MainWindow main, string directory, Action<bool, string> check, Action<Window, string> capture)
    {
        var platform = new Platform(); var invoked = new List<CompanionCommand>();
        using var manager = new GlobalShortcuts(IntPtr.Zero, invoked.Add, platform);
        var defaults = new ShortcutOptions();
        check(manager.Apply(defaults).All(r => r.Available) && platform.Registered.Values.All(v => (v.Modifiers & 0x4000) != 0),
            "Defaults register once with keyboard auto-repeat suppressed");
        check(manager.Handle(0x0312, new(GlobalShortcuts.FirstId + (int)CompanionCommand.NextAnswer)) && invoked.SequenceEqual(new[] { CompanionCommand.NextAnswer }),
            "Shortcut notifications route to their command");
        var invalid = defaults with { Bindings = defaults.Bindings.Select(b => b with { Gesture = "Ctrl+Alt+R" }).ToArray() };
        try { manager.Apply(invalid); throw new InvalidOperationException("Duplicate shortcut accepted"); }
        catch (InvalidDataException) { }
        check(platform.Registered.Count == 5, "Invalid replacements retain the working shortcut registrations");
        foreach (var invalidText in new[] { "A", "Win+R", "Ctrl+F12", "not-a-shortcut" })
        {
            try { GlobalShortcuts.Parse(invalidText); throw new InvalidOperationException("Invalid shortcut accepted"); }
            catch (InvalidDataException) { }
        }
        platform.BlockedKey = (uint)KeyInterop.VirtualKeyFromKey(Key.R);
        var registrations = manager.Apply(defaults);
        check(registrations.Single(r => r.Command == CompanionCommand.ToggleReaders) is { Available: false } && platform.Registered.Count == 4,
            "Conflicts are reported for the affected command while other shortcuts keep working");
        check(!manager.Handle(0x0312, new(GlobalShortcuts.FirstId + (int)CompanionCommand.ToggleReaders)), "Unavailable shortcuts cannot dispatch");
        var settings = new ShortcutSettings(directory); settings.Save(defaults);
        check(settings.Load().Bindings.SequenceEqual(defaults.Bindings), "Shortcut settings survive a fresh store instance");
        var original = File.ReadAllText(Path.Combine(directory, "shortcuts.json"));
        try { settings.Save(invalid); throw new InvalidOperationException("Invalid settings accepted"); } catch (InvalidDataException) { }
        check(File.ReadAllText(Path.Combine(directory, "shortcuts.json")) == original, "Invalid settings do not replace the saved preferences");
        manager.Dispose(); check(platform.Registered.Count == 0 && !manager.Handle(0x0312, new(GlobalShortcuts.FirstId)), "Disposal unregisters owned shortcuts and stops dispatch");

        // Real registration/conflict/release on invisible, test-owned HWNDs;
        // no physical keystrokes are generated and default user shortcuts are untouched.
        using var firstWindow = new HwndSource(new HwndSourceParameters("Shortcut registration probe") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        using var secondWindow = new HwndSource(new HwndSourceParameters("Shortcut conflict probe") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        using var first = new GlobalShortcuts(firstWindow.Handle, _ => { }, new WindowsShortcutPlatform());
        using var second = new GlobalShortcuts(secondWindow.Handle, _ => { }, new WindowsShortcutPlatform());
        ShortcutOptions? probe = null;
        for (var function = 24; function >= 13 && probe is null; function--)
        {
            var candidate = defaults with { Bindings = defaults.Bindings.Select(b => b with { Gesture = b.Command == CompanionCommand.ToggleControls ? $"Ctrl+Alt+Shift+F{function}" : "" }).ToArray() };
            if (first.Apply(candidate).Any(r => r.Available)) probe = candidate;
        }
        check(probe is not null, "A real Windows shortcut can be registered on a hidden test window");
        check(second.Apply(probe!).All(r => !r.Available), "Windows reports the deliberate cross-window shortcut conflict");
        first.Dispose();
        check(second.Apply(probe!).Any(r => r.Available), "Released Windows shortcuts can be registered by another window");

        var panel = main.AddReader(); var id = main.Session.DocumentId; var count = main.Panels.Count;
        main.ExecuteShortcut(CompanionCommand.ToggleReaders);
        check(main.Panels.All(p => !p.IsVisible) && main.Panels.Count == count, "Hide readers retains every reader instance");
        main.ExecuteShortcut(CompanionCommand.ToggleReaders);
        check(main.Panels.All(p => p.IsVisible && !p.ShowInTaskbar && NativeWindows.IsToolWindow(p)) && main.Session.DocumentId == id,
            "Show readers restores the same ghost windows and document");
        main.ExecuteShortcut(CompanionCommand.ToggleControls); check(!main.IsVisible, "Controls can hide through a shortcut");
        main.ExecuteShortcut(CompanionCommand.ToggleControls); check(main.IsVisible, "Controls can return through a shortcut");
        check(!main.Recorder.HasSession && !main.Voice.Running, "Reader commands never start microphone or recording");
        main.SettingsTab.IsSelected = true; main.ShortcutsTab.IsSelected = true;
        await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        capture(main, Path.Combine(directory, "shortcuts.png"));
        panel.SetChromeVisibility(true); capture(panel, Path.Combine(directory, "sentence-controls.png"));
    }
}
