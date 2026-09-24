using System.Windows.Threading;

namespace SecondBrain.App;

// Owned by the UI dispatcher; frame callbacks can only post generation-bound observations.
internal sealed class MeetingVisuals(Dispatcher dispatcher) : IDisposable
{
    public MeetingWindow? Target { get; private set; }
    public bool Enabled { get; private set; }
    public bool Listening { get; private set; }
    public bool Running => capture is not null;
    public long Generation => generation;
    public DateTimeOffset? LastFrame { get; private set; }
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public string Status { get; private set; } = "Off · listening uses system audio and microphone.";
    public event Action? Changed;
    internal Func<MeetingWindow, Action<MeetingFrame>, Action<string>, IMeetingCapture> Factory { get; set; } = (w, f, e) => new MeetingWindowCapture(w, f, e);
    private IMeetingCapture? capture;
    private long generation;
    private DateTimeOffset started;
    public void Select(MeetingWindow? selected)
    {
        dispatcher.VerifyAccess();
        if (selected is null) return; // A cancelled picker is a no-op, including during capture.
        Stop(); Target = selected; Refresh();
    }
    public void Enable(bool value) { dispatcher.VerifyAccess(); Stop(); Enabled = value; Refresh(); }
    public void Clear() { dispatcher.VerifyAccess(); Stop(); Target = null; Enabled = false; Refresh(); }
    public void Listen(bool active) { dispatcher.VerifyAccess(); Stop(); Listening = active; Refresh(); }
    private void Refresh()
    {
        if (!Enabled) Status = "Off · listening uses system audio and microphone.";
        else if (Target is null) Status = "Choose a meeting window. Audio-only listening continues.";
        else if (!Listening) Status = "Ready · window selected; capture starts only with listening.";
        else
        {
            var identity = generation; started = DateTimeOffset.UtcNow;
            try
            {
                capture = Factory(Target, frame =>
                {
                    // Validate that this is an actual nonblank frame without retaining its contents.
                    var visible = false;
                    for (var i = 0; i < frame.Pixels.Length; i += 256)
                        if (frame.Pixels[i] > 8 || frame.Pixels[Math.Min(i + 1, frame.Pixels.Length - 1)] > 8 || frame.Pixels[Math.Min(i + 2, frame.Pixels.Length - 1)] > 8) { visible = true; break; }
                    var at = frame.At; var width = frame.Width; var height = frame.Height;
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        if (identity != generation || capture is null) return;
                        LastFrame = visible ? at : null; FrameWidth = width; FrameHeight = height;
                        Status = visible ? "Local window capture active · frames are not saved or sent." : "Window is blank or protected · audio-only fallback.";
                        Changed?.Invoke();
                    });
                }, error => _ = dispatcher.BeginInvoke(() => { if (identity == generation) Fail(error); }));
                Status = "Starting local window capture…";
            }
            catch { Fail("Window capture is unavailable. Choose the window again to retry."); return; }
        }
        Changed?.Invoke();
    }
    public void Tick()
    {
        dispatcher.VerifyAccess();
        if (capture is null) return;
        if (Target?.Available != true) Fail("Window closed or minimized. Choose the window again when ready.");
        else if (DateTimeOffset.UtcNow - (LastFrame ?? started) > TimeSpan.FromSeconds(5)) Fail("No usable window frames. Choose the window again to retry.");
    }
    private void Fail(string reason) { Stop(); Status = reason + " Audio-only listening continues."; Changed?.Invoke(); }
    private void Stop() { generation++; LastFrame = null; FrameWidth = FrameHeight = 0; var old = capture; capture = null; old?.Dispose(); }
    public void Dispose() { dispatcher.VerifyAccess(); Stop(); Listening = false; }
}
