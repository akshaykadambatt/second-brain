using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SecondBrain.App;

public partial class MainWindow
{
    private GlobalShortcuts? globalShortcuts;
    private ShortcutOptions shortcutOptions = new();
    private string? shortcutLoadWarning;
    private readonly Dictionary<CompanionCommand, TextBox> shortcutInputs = [];
    internal static string CommandLabel(CompanionCommand command) => command switch
    {
        CompanionCommand.ToggleControls => "Show / hide controls", CompanionCommand.ToggleReaders => "Show / hide readers",
        CompanionCommand.PreviousAnswer => "Previous answer", CompanionCommand.NextAnswer => "Next answer", _ => "Resume voice following"
    };
    private void InitializeShortcutFields()
    {
        try { shortcutOptions = new ShortcutSettings(dataDirectory).Load(); }
        catch (Exception) { shortcutLoadWarning = "Saved shortcuts could not be read. Defaults are active; save to replace the unreadable file."; }
        foreach (var binding in shortcutOptions.Bindings)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(230) });
            row.Children.Add(new TextBlock { Text = CommandLabel(binding.Command), VerticalAlignment = VerticalAlignment.Center });
            var input = new TextBox { Text = binding.Gesture, MaxLength = 80 };
            System.Windows.Automation.AutomationProperties.SetName(input, CommandLabel(binding.Command) + " shortcut");
            Grid.SetColumn(input, 1); row.Children.Add(input); shortcutInputs.Add(binding.Command, input); ShortcutRows.Children.Add(row);
        }
    }
    private void InitializeShortcuts()
    {
        globalShortcuts = new(new WindowInteropHelper(this).Handle, ExecuteShortcut,
            hiddenTestMode ? new ShortcutTests.Platform() : new WindowsShortcutPlatform());
        ShowShortcutRegistrations(globalShortcuts.Apply(shortcutOptions));
    }
    private void ShowShortcutRegistrations(ShortcutRegistration[] registrations)
    { ShortcutStatus.Text = (shortcutLoadWarning is null ? "" : shortcutLoadWarning + "\n") + string.Join("\n", registrations.Select(r => $"{CommandLabel(r.Command)}: {r.Status}")); }
    private void SaveShortcuts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = new ShortcutOptions { Bindings = shortcutInputs.Select(p => new CommandShortcut(p.Key, p.Value.Text.Trim())).ToArray() };
            new ShortcutSettings(dataDirectory).Save(next); shortcutOptions = next; shortcutLoadWarning = null;
            if (globalShortcuts is not null) ShowShortcutRegistrations(globalShortcuts.Apply(next));
        }
        catch (Exception ex) { ShortcutStatus.Text = "Shortcuts were not applied: " + ex.Message; }
    }
    private void DefaultShortcuts_Click(object sender, RoutedEventArgs e)
    { foreach (var binding in new ShortcutOptions().Bindings) shortcutInputs[binding.Command].Text = binding.Gesture; ShortcutStatus.Text = "Defaults restored in the form. Save shortcuts to apply them."; }
    internal void ExecuteShortcut(CompanionCommand command)
    {
        if (closing || storageBusy) return;
        switch (command)
        {
            case CompanionCommand.ToggleControls: if (IsVisible) Hide(); else ShowControls(); break;
            case CompanionCommand.ToggleReaders:
                if (panels.Count == 0) AddReader();
                else if (panels.Any(p => p.IsVisible)) { foreach (var panel in panels) panel.Hide(); }
                else { foreach (var panel in panels) { panel.ShowActivated = false; panel.Show(); } }
                break;
            case CompanionCommand.PreviousAnswer: Companion?.Navigate(-1); break;
            case CompanionCommand.NextAnswer: Companion?.Navigate(1); break;
            case CompanionCommand.ResumeFollowing: ResumeFollowing_Click(this, new RoutedEventArgs()); break;
        }
    }
}
