using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SecondBrain.App;

public partial class MainWindow
{
    private void LiveLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AnswerWorkspace is null || SessionWorkspace is null) return;
        var wide = e.NewSize.Width >= 820;
        LiveLayout.ColumnDefinitions[1].Width = new GridLength(wide ? 290 : 0);
        Grid.SetColumn(SessionWorkspace, wide ? 1 : 0);
        Grid.SetRow(SessionWorkspace, wide ? 0 : 1);
        AnswerWorkspace.Margin = wide ? new Thickness(0, 0, 18, 0) : new Thickness(0, 0, 0, 20);
    }

    private void LiveQuestion_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || !LiveAsk.IsEnabled) return;
        e.Handled = true;
        LiveAsk_Click(sender, new RoutedEventArgs());
    }

    private void VaultQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || !VaultSearch.IsEnabled) return;
        e.Handled = true;
        VaultSearch_Click(sender, new RoutedEventArgs());
    }
}
