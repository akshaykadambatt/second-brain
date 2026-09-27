using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SecondBrain.App;

internal sealed record DisplayChoice(nint Handle, int Width, int Height, string Label)
{
    internal static DisplayChoice[] List()
    {
        var list = new List<DisplayChoice>();
        Native.EnumDisplayMonitors(0, 0, (nint handle, nint dc, ref Native.Rect rect, nint data) =>
        {
            var info = new Native.Info { Size = Marshal.SizeOf<Native.Info>() };
            if (Native.GetMonitorInfo(handle, ref info)) list.Add(new(handle, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
                $"Display {list.Count + 1} · {info.Monitor.Right - info.Monitor.Left} × {info.Monitor.Bottom - info.Monitor.Top}" + ((info.Flags & 1) != 0 ? " · primary" : "")));
            return true;
        }, 0);
        return list.ToArray();
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Info { public int Size; public Rect Monitor, Work; public uint Flags; }
        internal delegate bool Callback(nint handle, nint dc, ref Rect rect, nint data);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, Callback callback, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref Info info);
    }
}
internal sealed record VideoSelection(DisplayChoice Display, bool NativeResolution)
{
    internal (int Width, int Height) Size
    {
        get
        {
            if (Display.Width < 2 || Display.Height < 2 || Display.Width > 7680 || Display.Height > 4320) throw new InvalidOperationException("This display size is unsupported.");
            var scale = NativeResolution ? 1 : Math.Min(1, Math.Min(1920d / Display.Width, 1080d / Display.Height));
            return (Math.Max(2, (int)(Display.Width * scale) / 2 * 2), Math.Max(2, (int)(Display.Height * scale) / 2 * 2));
        }
    }
}
internal sealed class DisplayPicker : Window
{
    internal VideoSelection? Selection { get; private set; }
    internal DisplayPicker(Window owner)
    {
        Owner = owner; Title = "Record one whole display"; Width = 500; Height = 310; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Choose the display to record", FontWeight = FontWeights.Bold, FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = "The whole selected display is saved locally with this meeting. Recording starts only after you choose Record.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14) });
        var choices = new ComboBox { ItemsSource = DisplayChoice.List(), DisplayMemberPath = "Label", SelectedIndex = -1 }; panel.Children.Add(choices);
        var native = new CheckBox { Content = "Use native resolution (larger files)", Margin = new Thickness(0, 14, 0, 8) }; panel.Children.Add(native);
        panel.Children.Add(new TextBlock { Text = "Default: fit within 1080p · 30 frames per second", FontSize = 12 });
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) }; panel.Children.Add(buttons);
        var record = new Button { Content = "Record", IsEnabled = false, Margin = new Thickness(0, 0, 12, 0) }; var cancel = new Button { Content = "Cancel", IsCancel = true }; buttons.Children.Add(record); buttons.Children.Add(cancel);
        choices.SelectionChanged += (_, _) => record.IsEnabled = choices.SelectedItem is DisplayChoice;
        record.Click += (_, _) => { if (choices.SelectedItem is DisplayChoice display) { Selection = new(display, native.IsChecked == true); DialogResult = true; } };
    }
}
internal sealed record DisplayFrame(double Clock, int Width, int Height, byte[] Pixels);
internal interface IDisplayCapture : IDisposable { }
internal sealed class DisplayVideoCapture : IDisplayCapture
{
    private readonly object gate = new();
    private readonly Action<DisplayFrame> observe;
    private readonly Action<string> failed;
    private IDirect3DDevice? device; private GraphicsCaptureItem? item; private Direct3D11CaptureFramePool? pool; private GraphicsCaptureSession? session;
    private bool disposed; private int processing; private double last;
    internal DisplayVideoCapture(DisplayChoice display, Action<DisplayFrame> observe, Action<string> failed)
    {
        this.observe = observe; this.failed = failed;
        try
        {
            if (!GraphicsCaptureSession.IsSupported() || !DisplayChoice.List().Any(d => d.Handle == display.Handle && d.Width == display.Width && d.Height == display.Height)) throw new InvalidOperationException("The selected display is no longer available.");
            device = MeetingWindowCapture.CreateDevice(); item = MeetingWindowCapture.CreateItem(display.Handle, true);
            if (item.Size.Width != display.Width || item.Size.Height != display.Height) throw new InvalidOperationException("Display size changed. Choose the display again.");
            item.Closed += Closed; pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            pool.FrameArrived += Arrived; session = pool.CreateCaptureSession(item); session.StartCapture();
        }
        catch { Dispose(); throw; }
    }
    private void Closed(GraphicsCaptureItem sender, object args) => Fail("Selected display disconnected.");
    private void Fail(string reason) { Dispose(); failed(reason); }
    private async void Arrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Interlocked.Exchange(ref processing, 1) != 0) return; byte[]? pixels = null;
        try
        {
            Direct3D11CaptureFrame? frame; lock (gate) { if (disposed) return; frame = sender.TryGetNextFrame(); }
            using (frame)
            {
                if (frame is null) return;
                var clock = frame.SystemRelativeTime.TotalSeconds;
                if (clock - last < 1d / 35) return; last = clock;
                if (frame.ContentSize.Width != item?.Size.Width || frame.ContentSize.Height != item?.Size.Height) { Fail("Display size changed. Start a new recording for the new size."); return; }
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                var width = frame.ContentSize.Width; var height = frame.ContentSize.Height;
                if (bitmap.PixelWidth != width || bitmap.PixelHeight != height) { Fail("Display surface size changed."); return; }
                var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(width * height * 4))); bitmap.CopyToBuffer(buffer); pixels = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                lock (gate) { if (!disposed) observe(new(clock, width, height, pixels)); }
            }
        }
        catch (Exception) { if (!disposed) Fail("Display capture failed. Listening continues."); }
        finally { if (pixels is not null) Array.Clear(pixels); Volatile.Write(ref processing, 0); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            if (item is not null) item.Closed -= Closed; if (pool is not null) pool.FrameArrived -= Arrived;
            session?.Dispose(); pool?.Dispose(); device?.Dispose(); session = null; pool = null; device = null;
        }
    }
}
