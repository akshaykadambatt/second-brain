using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class SmokeTest
{
    // Executes the production window commands and lifecycle, not a parallel UI model.
    public static async Task Run(MainWindow window, string directory, string phase)
    {
        var checks = new List<string>();
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                checks.Add(message);
            }
            if (phase == "verify")
            {
                Check(!window.Settings.RememberReaderPosition, "Preference survives process restart");
                Check(window.Settings.ReaderPlacement?.Width == 520, "Reader geometry survives process restart");
                window.RememberPosition.IsChecked = true;
            }
            window.OpenReaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reader = window.Reader!;
            Check(reader.IsVisible, "Open button shows reader");
            Check(reader.Topmost, "Reader floats above normal windows");
            Check(((Grid)reader.Content).Children.Count == 0, "Reader content is empty");
            if (phase == "verify") Check(reader.Width == 520, "Stored geometry is applied");
            window.OpenReaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(reader, window.Reader), "Repeated open reuses one reader");
            reader.Width = 520;
            reader.Height = 280;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.CloseReaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.Reader is null && !reader.IsVisible, "Close button destroys reader");
            Check(window.ReaderState.Text == "Closed" && window.StatusText.Text.StartsWith("Reader closed", StringComparison.Ordinal), "Reader close updates visible state");
            window.RememberPosition.IsChecked = false;
            var persisted = new SettingsStore(directory).Load(out var warning);
            Check(warning is null && !persisted.RememberReaderPosition, "Preference is saved");
            Capture(window, Path.Combine(directory, "control-window.png"));
            window.OpenReader();
            Capture(window.Reader!, Path.Combine(directory, "reader-window.png"));
            var finalReader = window.Reader;
            window.Close();
            Check(finalReader is { IsVisible: false }, "Closing main window closes reader");
            File.WriteAllText(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString() }));
            Application.Current.Shutdown(1);
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
