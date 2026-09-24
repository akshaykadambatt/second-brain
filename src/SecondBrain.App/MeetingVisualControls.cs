using System.Windows;
using System.Windows.Controls;

namespace SecondBrain.App;

public partial class MainWindow
{
    internal MeetingVisuals Visuals { get; private set; } = null!;
    private void InitializeVisuals()
    {
        Visuals = new(Dispatcher);
        Visuals.Changed += () => { VisualCaptureStatus.Text = Visuals.Status; VisualWindowLabel.Text = Visuals.Target?.Label ?? "No window selected"; };
    }
    private void VisualEnable_Click(object sender, RoutedEventArgs e) => Visuals.Enable(VisualHintsCheck.IsChecked == true);
    private void VisualClear_Click(object sender, RoutedEventArgs e) { Visuals.Clear(); VisualHintsCheck.IsChecked = false; }
    private void VisualChoose_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListBox { DisplayMemberPath = "Label", MinHeight = 200, Margin = new(0, 12, 0, 12) };
        var dialog = new Window { Title = "Choose meeting window", Owner = this, Width = 620, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new DockPanel { Margin = new(20) }; dialog.Content = panel;
        var title = new TextBlock { Text = "Select one window for optional local name hints. Selection alone does not start capture. Frames are never saved or sent.", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var refresh = new Button { Content = "Refresh windows", Margin = new(0, 0, 8, 0) };
        var choose = new Button { Content = "Use selected window", IsDefault = true, Margin = new(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        buttons.Children.Add(refresh); buttons.Children.Add(choose); buttons.Children.Add(cancel); panel.Children.Add(list);
        void Reload() => list.ItemsSource = MeetingWindow.List();
        refresh.Click += (_, _) => Reload(); choose.Click += (_, _) => { if (list.SelectedItem is MeetingWindow window && window.Available) dialog.DialogResult = true; };
        Reload(); if (dialog.ShowDialog() == true) Visuals.Select(list.SelectedItem as MeetingWindow);
    }
}
