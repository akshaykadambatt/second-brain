using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SecondBrain.App;

internal sealed record MeetingWindow(nint Handle, int ProcessId, long Started, string ProcessName, string Title)
{
    public string Label => $"{Title} · {ProcessName}";
    public bool Available
    {
        get
        {
            try { Native.GetWindowThreadProcessId(Handle, out var id); using var process = Process.GetProcessById(ProcessId);
                return id == ProcessId && process.StartTime.ToUniversalTime().Ticks == Started && Native.IsWindowVisible(Handle) && !Native.IsIconic(Handle); }
            catch { return false; }
        }
    }
    public static MeetingWindow[] List(bool includeSelf = false)
    {
        var windows = new List<MeetingWindow>();
        Native.EnumWindows((handle, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(handle) || Native.GetWindow(handle, 4) != 0) return true;
                Native.GetWindowThreadProcessId(handle, out var id);
                if (!includeSelf && id == Environment.ProcessId) return true;
                var title = new StringBuilder(512); Native.GetWindowText(handle, title, title.Capacity);
                if (title.Length == 0) return true;
                using var process = Process.GetProcessById((int)id);
                windows.Add(new(handle, (int)id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName, title.ToString()));
            }
            catch { /* A window can disappear during enumeration. */ }
            return true;
        }, 0);
        return windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static class Native
    {
        internal delegate bool EnumCallback(nint hwnd, nint parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    }
}

// Pixels are owned by the capture worker and cleared immediately after this synchronous callback.
internal sealed record MeetingFrame(DateTimeOffset At, int Width, int Height, byte[] Pixels);
internal interface IMeetingCapture : IDisposable { }
internal sealed class MeetingWindowCapture : IMeetingCapture
{
    private readonly object gate = new();
    private readonly MeetingWindow target;
    private readonly Action<MeetingFrame> observe;
    private readonly Action<string> failed;
    private IDirect3DDevice? device;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private bool disposed;
    private int processing;
    private long lastFrame;
    public MeetingWindowCapture(MeetingWindow target, Action<MeetingFrame> observe, Action<string> failed)
    {
        this.target = target; this.observe = observe; this.failed = failed;
        try
        {
            if (!target.Available || !GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("Window capture is unavailable.");
            device = CreateDevice(); item = CreateItem(target.Handle); ValidateSize(item.Size.Width, item.Size.Height);
            item.Closed += ItemClosed;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            pool.FrameArrived += FrameArrived;
            session = pool.CreateCaptureSession(item); session.IsCursorCaptureEnabled = false; session.StartCapture();
        }
        catch { Dispose(); throw; }
    }
    private static void ValidateSize(int width, int height)
    { if (width < 1 || height < 1 || width > 7680 || height > 4320) throw new InvalidOperationException("Window dimensions are unsupported."); }
    private void ItemClosed(GraphicsCaptureItem sender, object args) => Fail("Selected window closed.");
    private void Fail(string reason) { Dispose(); failed(reason); }
    private async void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Interlocked.Exchange(ref processing, 1) != 0) return;
        byte[]? pixels = null;
        try
        {
            Direct3D11CaptureFrame? frame;
            lock (gate) { if (disposed) return; frame = sender.TryGetNextFrame(); }
            using (frame)
            {
                if (frame is null) return;
                if (!target.Available) { Fail("Selected window is unavailable or minimized."); return; }
                if (Stopwatch.GetElapsedTime(lastFrame).TotalMilliseconds < 500) return;
                lastFrame = Stopwatch.GetTimestamp();
                var width = frame.ContentSize.Width; var height = frame.ContentSize.Height; ValidateSize(width, height);
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                // Resize first; never interpret pixels copied from a differently sized surface.
                if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
                { lock (gate) { if (!disposed) sender.Recreate(device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize); } return; }
                var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(width * height * 4)));
                bitmap.CopyToBuffer(buffer); pixels = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                lock (gate) { if (!disposed) observe(new(DateTimeOffset.UtcNow, width, height, pixels)); }
            }
        }
        catch (Exception) { if (!disposed) Fail("Window capture failed. Audio-only listening continues."); }
        finally { if (pixels is not null) Array.Clear(pixels); Volatile.Write(ref processing, 0); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            if (item is not null) item.Closed -= ItemClosed;
            if (pool is not null) pool.FrameArrived -= FrameArrived;
            session?.Dispose(); pool?.Dispose(); device?.Dispose(); session = null; pool = null; device = null; item = null;
        }
    }
    private static GraphicsCaptureItem CreateItem(nint window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var text));
        nint factory = 0, result = 0;
        try
        {
            var iid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out factory));
            var method = Marshal.GetDelegateForFunctionPointer<CreateWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * nint.Size));
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(method(factory, window, ref itemId, out result));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(result);
        }
        finally { if (result != 0) Marshal.Release(result); if (factory != 0) Marshal.Release(factory); WindowsDeleteString(text); }
    }
    private static IDirect3DDevice CreateDevice()
    {
        nint d3d = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out d3d, out _, out context));
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally { foreach (var pointer in new[] { inspectable, dxgi, context, d3d }) if (pointer != 0) Marshal.Release(pointer); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateWindow(nint self, nint window, ref Guid iid, out nint result);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint text, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags, nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
}
