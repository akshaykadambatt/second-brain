using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record TeamsWindowObservation(bool Supported, string[] Names, string Status);
internal static class TeamsWindowHints
{
    internal static TeamsWindowObservation Read(MeetingWindow target, string[] roster)
    {
        if (!target.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) || !target.Available)
            return new(false, [], "Choose a visible Chrome Teams window.");
        if (roster.Length == 0) return new(false, [], "Add participant names to the meeting brief for exact matching.");
        try
        {
            var root = AutomationElement.FromHandle(target.Handle);
            var bounds = root.Current.BoundingRectangle;
            var watch = Stopwatch.StartNew(); var visited = 0; var truncated = false;
            var documents = new List<AutomationElement>();
            Walk(root, 0, node => { if (node.Current.ControlType == ControlType.Document) { documents.Add(node); return false; } return true; });
            // Document value must expose its real URL; an edited address bar or window title is insufficient.
            var supported = documents.Where(d => !d.Current.IsOffscreen && d.GetCurrentPropertyValue(ValuePattern.ValueProperty) is string value
                && TeamsSpeakingLabels.SupportedAddress(value)).ToArray();
            if (truncated || supported.Length != 1) return new(false, [], "Teams page URL is unavailable or unsupported; audio labels only.");
            var names = new List<string>(); var unknownSpeaking = false;
            Walk(supported[0], 0, node =>
            {
                var properties = node.Current;
                if (properties.IsOffscreen || properties.BoundingRectangle.IsEmpty || !bounds.Contains(properties.BoundingRectangle)) return false;
                if (properties.ControlType == ControlType.Group || properties.ControlType == ControlType.Button || properties.ControlType == ControlType.Custom)
                {
                    var label = properties.Name;
                    if (properties.HelpText.Equals("Speaking", StringComparison.OrdinalIgnoreCase)) label += " is speaking";
                    var name = TeamsSpeakingLabels.Name(label, roster);
                    if (name is not null) names.Add(name);
                    else if (label.EndsWith(" is speaking", StringComparison.OrdinalIgnoreCase) || label.EndsWith(", speaking", StringComparison.OrdinalIgnoreCase)
                        || label.StartsWith("Speaking: ", StringComparison.OrdinalIgnoreCase)) unknownSpeaking = true;
                }
                return true;
            });
            if (truncated || !target.Available) return new(false, [], "Window inspection was incomplete; audio labels only.");
            var unique = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Count > unique.Length) return new(true, [], "Repeated speaking names are ambiguous; no name assigned.");
            if (unknownSpeaking) return new(true, [], "Unrecognized speaking participant; no name assigned.");
            return new(true, unique, unique.Length == 1 ? "One explicit speaking indicator observed (experimental)."
                : unique.Length > 1 ? "Multiple speaking indicators; no name assigned." : "No supported speaking indicator; audio labels only.");

            void Walk(AutomationElement node, int depth, Func<AutomationElement, bool> inspect)
            {
                if (depth > 30 || ++visited > 1800 || watch.ElapsedMilliseconds > 450) { truncated = true; return; }
                if (!inspect(node)) return;
                var walker = TreeWalker.ControlViewWalker;
                for (var child = walker.GetFirstChild(node); child is not null && !truncated; child = walker.GetNextSibling(child)) Walk(child, depth + 1, inspect);
            }
        }
        catch { return new(false, [], "Chrome accessibility is unavailable; audio labels only."); }
    }
}

// UI-owned coordinator. Slow/hung accessibility providers never block audio or shutdown.
internal sealed class TeamsHintSession(MeetingVisuals visuals, Action<string> status)
{
    private SpeakerHintTimeline? timeline;
    private double origin, nextPoll;
    private long epoch, captureEpoch;
    private string[] roster = [];
    private Task<TeamsWindowObservation>? pending;
    private bool sampling;
    internal Func<MeetingWindow, string[], TeamsWindowObservation> Read { get; set; } = TeamsWindowHints.Read;
    public void Start(Guid sessionId, double clockOrigin, string[] participants)
    { End(); timeline = new(sessionId); origin = clockOrigin; roster = participants; sampling = true; captureEpoch = visuals.Generation; nextPoll = 0; }
    public void StopSampling() { sampling = false; epoch++; }
    public void End() { StopSampling(); timeline?.Clear(); timeline = null; }
    public SpeakerNameHint? Resolve(TranscriptDetail detail) => timeline?.Resolve(detail);
    public async void Tick()
    {
        if (!sampling || timeline is null) return;
        if (captureEpoch != visuals.Generation) { timeline.Clear(); captureEpoch = visuals.Generation; epoch++; }
        var now = AudioClock.Now;
        if (now < nextPoll) return; nextPoll = now + .5;
        var currentTimeline = timeline;
        if (!visuals.Running || visuals.LastFrame is not { } frameAt || DateTimeOffset.UtcNow - frameAt > TimeSpan.FromSeconds(1.2) || visuals.Target is not { } target)
        { currentTimeline.Observe(Math.Max(0, now - origin), []); status("Screen names off or no fresh frame · audio labels only."); return; }
        if (pending is { IsCompleted: false }) { currentTimeline.Observe(Math.Max(0, now - origin), []); return; }
        var requestEpoch = epoch; var requestCapture = visuals.Generation;
        pending = Task.Run(() => Read(target, roster));
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromMilliseconds(800));
            if (!sampling || requestEpoch != epoch || requestCapture != visuals.Generation || currentTimeline != timeline) return;
            if (visuals.LastFrame is not { } fresh || DateTimeOffset.UtcNow - fresh > TimeSpan.FromSeconds(1.2)) result = new(false, [], "Window frame became stale; audio labels only.");
            currentTimeline.Observe(Math.Max(0, now - origin), result.Supported ? result.Names : []); status(result.Status);
        }
        catch (Exception)
        {
            if (!sampling || requestEpoch != epoch) return;
            currentTimeline.Observe(Math.Max(0, now - origin), []); status("Window inspection timed out or failed; audio continues.");
        }
    }
}
