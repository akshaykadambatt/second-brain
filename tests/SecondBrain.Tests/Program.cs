using System.Text.Json;
using SecondBrain.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var run = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(run);
var results = new List<object>();
var failures = 0;
void Test(string name, Action action)
{
    try { action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
string Folder(string name) { var path = Path.Combine(run, name); Directory.CreateDirectory(path); return path; }

Test("Missing settings use defaults without writing", () =>
{
    var store = new SettingsStore(Folder("defaults"));
    var value = store.Load(out var warning);
    Assert(value.RememberReaderPosition && value.ReaderPlacement is null && warning is null, "Incorrect defaults");
    Assert(!File.Exists(store.FilePath), "Load unexpectedly wrote a file");
});
Test("Settings round-trip across independent store instances", () =>
{
    var path = Folder("roundtrip");
    var value = new AppSettings { RememberReaderPosition = false, ReaderPlacement = new(100, 120, 520, 280), TimedWordsPerMinute = 210 };
    new SettingsStore(path).Save(value);
    Assert(JsonSerializer.Serialize(new SettingsStore(path).Load(out var warning)) == JsonSerializer.Serialize(value) && warning is null, "Round-trip mismatch");
});
Test("Repeated saves replace complete documents", () =>
{
    var store = new SettingsStore(Folder("replace"));
    store.Save(new());
    store.Save(new() { RememberReaderPosition = false });
    Assert(!store.Load(out _).RememberReaderPosition, "Old settings retained");
    Assert(!File.Exists(store.FilePath + ".tmp"), "Temporary file was not consumed");
});
Test("Malformed and unsupported settings report warnings", () =>
{
    var store = new SettingsStore(Folder("corrupt"));
    foreach (var text in new[] { "{", "null", "{\"SchemaVersion\":99}", "{\"ReaderPlacement\":{\"Width\":-1,\"Height\":300}}" })
    {
        File.WriteAllText(store.FilePath, text);
        Assert(store.Load(out var warning).RememberReaderPosition && warning is not null, "Bad settings were silently accepted");
    }
});
Test("Save failure is surfaced to caller", () =>
{
    var path = Path.Combine(run, "not-a-directory");
    File.WriteAllText(path, "occupied");
    try { new SettingsStore(path).Save(new()); throw new Exception("Save unexpectedly succeeded"); }
    catch (IOException) { }
});
Test("Geometry rejects non-finite and unusable values", () =>
{
    Assert(!new WindowPlacement(double.NaN, 0, 500, 300).IsValid, "NaN accepted");
    Assert(!new WindowPlacement(0, 0, double.PositiveInfinity, 300).IsValid, "Infinity accepted");
    Assert(!new WindowPlacement(0, 0, 100, 100).IsValid, "Undersized window accepted");
});
Test("Diagnostic log writes and rotates", () =>
{
    var log = new DiagnosticLog(Folder("logging"));
    Assert(log.Write("one"), "Write failed");
    Assert(File.ReadAllText(log.FilePath).Contains("one"), "Entry missing");
    File.WriteAllText(log.FilePath, new string('x', 1_000_001));
    Assert(log.Write("two") && File.Exists(log.FilePath + ".previous"), "Rotation failed");
    Assert(File.ReadAllText(log.FilePath).Contains("two"), "New entry missing");
});
Test("Unavailable diagnostics do not throw", () =>
{
    var path = Path.Combine(run, "blocked-log");
    File.WriteAllText(path, "occupied");
    Assert(!new DiagnosticLog(path).Write("entry"), "Failure was not reported");
});
Test("Requirements have unique IDs, sprint assignments and completion evidence", () =>
{
    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "requirements.json")));
    var seen = new HashSet<string>();
    var sprints = new HashSet<int>();
    foreach (var row in doc.RootElement.GetProperty("requirements").EnumerateArray())
    {
        var id = row.GetProperty("id").GetString()!;
        Assert(seen.Add(id), "Duplicate ID " + id);
        var sprint = row.GetProperty("sprint").GetInt32();
        Assert(sprint is >= 0 and <= 10, "Invalid sprint " + id);
        sprints.Add(sprint);
        Assert(!string.IsNullOrWhiteSpace(row.GetProperty("acceptance").GetString()), "Missing acceptance " + id);
        var status = row.GetProperty("status").GetString();
        Assert(new[] { "planned", "in_progress", "passed", "blocked", "deferred" }.Contains(status), "Invalid status " + id);
        if (status == "passed")
        {
            var evidence = row.GetProperty("evidence").EnumerateArray().ToArray();
            Assert(evidence.Length > 0, "Missing evidence " + id);
            foreach (var item in evidence)
                Assert(File.Exists(Path.Combine(root, item.GetString()!)), "Evidence file absent " + id);
        }
        if (sprint > doc.RootElement.GetProperty("activeSprint").GetInt32()) Assert(status is "planned" or "deferred", "Future sprint unexpectedly marked implemented: " + id);
    }
    Assert(Enumerable.Range(0, 11).All(sprints.Contains), "A sprint has no requirements");
});
Test("Voice prototype excludes system capture, recognition fallback and unrelated AI capabilities", () =>
{
    var forbidden = new[] { "SpeechRecognitionEngine", "DictationGrammar", "WasapiLoopbackCapture", "DataFlow.Render", "Process.Start", "api.openai.com", "api.anthropic.com" };
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
    {
        var source = File.ReadAllText(file);
        foreach (var symbol in forbidden) Assert(!source.Contains(symbol, StringComparison.Ordinal), "Unexpected capability in " + file + ": " + symbol);
    }
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
    {
        var xml = System.Xml.Linq.XDocument.Load(file);
        Assert(xml.Descendants("PackageReference").All(p => new[] { "System.Speech", "NAudio.Wasapi", "System.Security.Cryptography.ProtectedData" }.Contains((string?)p.Attribute("Include"))), "Unexpected external runtime dependency");
    }
});
Test("Words retain identity across styling and manual selection", () =>
{
    var session = new ReaderSession(); session.Load("Hello world.\n\nThis is a test.");
    var id = session.DocumentId; var words = session.Words;
    session.Select(3); session.SetStyle(new ReaderStyle(FontSize: 38));
    Assert(session.Position == 3 && session.DocumentId == id && ReferenceEquals(words, session.Words), "Styling reset reading state");
    Assert(session.Words[1].Suffix == "\n\n", "Paragraphs lost");
});
Test("Repeated hypotheses cannot advance into a repeated sentence", () =>
{
    var session = new ReaderSession(); session.Load("Hello world hello world welcome back.");
    var follower = new SpeechFollower(session); follower.BeginUtterance();
    follower.Observe("hello world", false, .9f); follower.Observe("hello world", false, .9f);
    follower.Observe("hello world", true, .9f); follower.Observe("hello world", true, .9f);
    Assert(session.Position == 2, "Duplicate hypotheses advanced twice");
});
Test("Unrelated and low-confidence speech hold position", () =>
{
    var session = new ReaderSession(); session.Load(ReaderSession.Sample);
    var follower = new SpeechFollower(session); follower.BeginUtterance();
    follower.Observe("banana helicopter sunshine", true, .9f);
    follower.Observe("Today I want to talk", true, .1f);
    Assert(session.Position == 0, "Uncertain speech advanced");
});
Test("Final speech advances; manual recovery reanchors; no automatic backward jump", () =>
{
    var session = new ReaderSession(); session.Load("Today I want to talk about our next steps.");
    var follower = new SpeechFollower(session); follower.BeginUtterance();
    follower.Observe("today I want to talk", true, .9f);
    Assert(session.Position == 5, "Recognized phrase did not advance");
    follower.Observe("today I want", true, .9f);
    Assert(session.Position == 5, "Recognition moved backwards");
    session.Select(0); follower.BeginUtterance(); follower.Observe("today I want", true, .9f);
    Assert(session.Position == 3, "Manual recovery failed");
});
Test("Disconnected-display geometry fits in physical work area", () =>
{
    var placed = new PanelPlacement(90000, 90000, 3000, 2000).FitTo(new(0, 0, 1920, 1040));
    Assert(placed == new PanelPlacement(0, 0, 1920, 1040), "Recovery failed");
    var leftMonitor = new PanelPlacement(-1800, 100, 700, 500).FitTo(new(-1920, 0, 1920, 1040));
    Assert(leftMonitor.Left == -1800, "Negative monitor coordinate was lost");
});
Test("Deepgram revisions, finalized chunks and late duplicates retain correct anchors", () =>
{
    var session = new ReaderSession(); session.Load("hello world hello world welcome back");
    var progress = new DeepgramProgress(session);
    progress.Observe(new(0, .4, "hello", false, false, .95f));
    progress.Observe(new(0, .9, "hello world", false, false, .95f));
    progress.Observe(new(0, 1, "hello world", true, false, .95f));
    Assert(session.Position == 2, "First chunk mismatch");
    progress.Observe(new(0, 1, "hello world", true, false, .95f));
    Assert(session.Position == 2, "Final duplicate advanced");
    progress.Observe(new(1, 1, "hello world", true, true, .95f));
    Assert(session.Position == 4, "Next final chunk failed to reanchor");
    progress.Observe(new(0, 2, "hello world welcome back", true, true, .95f));
    Assert(session.Position == 4, "Late revision advanced");
});
Test("Deepgram manual recovery suppresses in-flight words until a final boundary", () =>
{
    var session = new ReaderSession(); session.Load("hello world welcome back");
    var progress = new DeepgramProgress(session);
    progress.Observe(new(0, 1, "hello world", true, true, .95f));
    session.Select(0); progress.Reanchor();
    progress.Observe(new(1, 1, "welcome back", false, false, .95f));
    Assert(session.Position == 0, "In-flight words defeated manual positioning");
    progress.Observe(new(1, 1.2, "", true, true, 0));
    progress.Observe(new(2.2, 1, "hello world", true, true, .95f));
    Assert(session.Position == 2, "Empty final did not release manual hold");
});
Test("Deepgram JSON separates results, metadata and service errors", () =>
{
    var result = DeepgramProtocol.Parse("""{"type":"Results","start":0,"duration":1.3,"is_final":true,"speech_final":true,"channel":{"alternatives":[{"transcript":"Hello world.","confidence":0.98}]}}""");
    Assert(result is { Text: "Hello world.", Final: true, SpeechFinal: true, Duration: 1.3 }, "Result was not decoded");
    Assert(DeepgramProtocol.Parse("""{"type":"Metadata","request_id":"test"}""") is null, "Metadata became speech");
    try { DeepgramProtocol.Parse("""{"type":"Error","description":"untrusted payload"}"""); throw new Exception("Error ignored"); }
    catch (InvalidOperationException ex) { Assert(!ex.Message.Contains("untrusted"), "Raw service payload exposed"); }
});
Test("Native float stereo converts to signed mono PCM with clipping and silence handling", () =>
{
    var input = new[] { .5f, .5f, -1f, -1f, float.NaN, 0f, 2f, 2f }.SelectMany(BitConverter.GetBytes).ToArray();
    var pcm = PcmAudio.ToMono16(input, 32, 2, true, out var level);
    var samples = Enumerable.Range(0, pcm.Length / 2).Select(i => BitConverter.ToInt16(pcm, i * 2)).ToArray();
    Assert(samples.SequenceEqual(new short[] { 16384, -32768, 0, 32767 }) && level == 100, "Float conversion corrupted audio");
    var silence = PcmAudio.ToMono16(new byte[12], 24, 2, false, out level);
    Assert(silence.All(b => b == 0) && level == 0, "Silence acquired noise");
});
Test("PCM16, PCM24 and PCM32 retain polarity and full-scale amplitude", () =>
{
    foreach (var bits in new[] { 16, 24, 32 })
    {
        var bytes = bits / 8;
        var input = new byte[bytes * 2];
        input[bytes - 1] = 128;
        for (var i = bytes; i < input.Length; i++) input[i] = 255;
        input[^1] = 127;
        var pcm = PcmAudio.ToMono16(input, bits, 1, false, out _);
        Assert(BitConverter.ToInt16(pcm, 0) == -32768 && BitConverter.ToInt16(pcm, 2) == 32767, "Integer conversion failed " + bits);
    }
    try { PcmAudio.ToMono16(new byte[3], 16, 1, false, out _); throw new Exception("Partial frame accepted"); }
    catch (InvalidOperationException) { }
});
Test("Line glide eases in and out without overshoot, then holds during silence", () =>
{
    var motion = new ReaderMotion(); motion.Reset(0); motion.Follow(44.8);
    var steps = new List<double>();
    for (var i = 0; i < 180; i++)
    {
        var old = motion.Position; motion.Step(1d / 60, 44.8);
        steps.Add(motion.Position - old);
        Assert(motion.Position >= old && motion.Position <= 44.8, "Glide reversed or overshot");
    }
    Assert(steps[0] < .25 && steps.Max() > steps[0] * 3, "No gentle acceleration");
    Assert(steps.Max() <= 44.8 * 2.2 / 60 + .001, "Line speed cap exceeded");
    Assert(!motion.Moving && Math.Abs(motion.Position - 44.8) < .01, "Did not settle on the selected line");
    for (var i = 0; i < 600; i++) motion.Step(1d / 60, 44.8);
    Assert(motion.Position == 44.8, "Continued scrolling without new progress");
});
Test("Burst updates preserve motion; a render stall cannot trigger catch-up", () =>
{
    var motion = new ReaderMotion(); motion.Follow(45);
    for (var i = 0; i < 15; i++) motion.Step(1d / 60, 45);
    var old = motion.Position; motion.Follow(225);
    Assert(motion.Position == old, "Retarget jumped immediately");
    motion.Step(2, 45);
    Assert(motion.Position - old <= 45 * 2.2 / 30 + .001, "Stall caused catch-up jump");
    var target = motion.Target; motion.Follow(0);
    Assert(motion.Target == target, "Voice target moved backwards");
    motion.Reset(0); motion.Step(1, 45);
    Assert(motion.Position == 0 && !motion.Moving, "Manual reposition retained old momentum");
});
Test("Glide distance is consistent at 30, 60 and 120 Hz", () =>
{
    var offsets = new List<double>();
    foreach (var hz in new[] { 30, 60, 120 })
    {
        var motion = new ReaderMotion(); motion.Follow(200);
        for (var i = 0; i < hz; i++) motion.Step(1d / hz, 45);
        offsets.Add(motion.Position);
    }
    Assert(offsets.Max() - offsets.Min() < .05, "Motion depends on refresh rate");
});
Test("Timed clock is refresh-rate independent and accelerates toward WPM", () =>
{
    var positions = new List<double>();
    foreach (var hz in new[] { 30, 60, 120 })
    {
        var session = new ReaderSession(); session.Load(ReaderSession.Sample); var player = new ReaderPlayback(session);
        player.SetSpeed(150); player.Play();
        for (var i = 0; i < hz * 10; i++) player.Tick(1d / hz);
        positions.Add(player.Cursor);
        Assert(player.Cursor > 24 && player.Cursor < 25 && session.Position == 24, "Incorrect integrated WPM");
    }
    Assert(positions.Max() - positions.Min() < .0001, "Timed playback depends on frame count");
});
Test("Timed pause, same-word override, speed changes, stalls and end are bounded", () =>
{
    var session = new ReaderSession(); session.Load("one two three four five six seven eight nine ten");
    var player = new ReaderPlayback(session); player.Play();
    for (var i = 0; i < 60; i++) player.Tick(1d / 60);
    var before = player.Cursor; player.Pause(); player.Tick(10);
    Assert(player.Cursor == before, "Pause moved position");
    player.Play(); player.SetSpeed(300); player.Tick(10);
    Assert(player.Cursor - before < .17, "Render stall jumped forward");
    session.Select(session.Position); Assert(!player.Playing, "Same-word click did not pause");
    player.Play(); for (var i = 0; i < 600; i++) player.Tick(1d / 60);
    Assert(session.Position == 10 && !player.Playing && player.Cursor == 10, "End of script did not stop");
    player.Play(); Assert(player.Playing && player.Cursor == 0, "Replay from end failed");
    player.UseVoice(); before = player.Cursor; player.Tick(1);
    Assert(!player.TimedMode && !player.Playing && player.Cursor == before, "Clock advanced in voice mode");
});
Test("Sentence navigation handles punctuation, abbreviations, decimals and paragraphs", () =>
{
    var session = new ReaderSession(); session.Load("Dr. Green measured 1.5 volts. \"Is it ready?\" Yes!\n\nNext section without punctuation\n\nFinal paragraph.");
    var starts = ReaderPlayback.SentenceStarts(session.Words);
    Assert(starts.SequenceEqual(new[] { 0, 5, 8, 9, 13 }), "Incorrect sentence boundaries: " + string.Join(',', starts));
    var player = new ReaderPlayback(session); player.Play(); player.Sentence(1);
    Assert(!player.Playing && session.Position == 5, "Next sentence did not pause at boundary");
    session.Select(7); player.Sentence(-1); Assert(session.Position == 5, "Previous should return to current sentence start mid-sentence");
    player.Sentence(-1); Assert(session.Position == 0, "Previous at start should return to prior sentence");
});
Test("Reading study varies order, covers all presets and requires ten distinct trials", () =>
{
    var first = Enumerable.Range(1, 5).Select(n => ReadingStudy.Trial(1, n, 36)).ToArray();
    var second = Enumerable.Range(1, 5).Select(n => ReadingStudy.Trial(2, n, 36)).ToArray();
    Assert(first.Take(3).Select(t => t.Width).Order().SequenceEqual(new[] { 28, 36, 48 }), "Missing comparison width");
    Assert(!first.Select(t => (t.Width, t.Font)).SequenceEqual(second.Select(t => (t.Width, t.Font))), "Session order did not vary");
    Assert(first.Skip(3).Select(t => t.Font).Order().SequenceEqual(new[] { 32, 38 }), "Missing comparison font");
    var row = new ReadingResult(DateTimeOffset.UtcNow, 1, 1, "Width", 36, 32, 150, 0, 0, 0, 3, 1, true, 60);
    Assert(!ReadingStudy.Complete(Enumerable.Repeat(row, 10)), "Repeated save falsely completed study");
    Assert(ReadingStudy.RecommendedWidth(new[] { row with { Width = 28, Mistakes = 2, Comfort = 5 }, row, row with { Width = 48, Comfort = 4 } }) == 48, "Recommendation did not rank errors before comfort");
});
Test("Dummy speech replays pauses, repeats, delays and skipped words through live progress adapter", () =>
{
    var session = new ReaderSession(); session.Load(ReaderSession.Sample);
    var source = new ScriptedSpeechSource(session);
    var events = new List<(string Label, int Position)>();
    source.Heard += (_, label) => events.Add((label, session.Position));
    for (var i = 0; i < 480; i++) source.Tick(1d / 60);
    Assert(!source.Running && session.Position == 15, "Replay did not reach expected final word");
    Assert(events.Count == 7 && events[2].Position == events[3].Position && events[4].Position == events[5].Position, "Duplicate or uncertain speech advanced");
    Assert(events.Zip(events.Skip(1)).All(pair => pair.Second.Position >= pair.First.Position), "Replay moved backwards");
});
var report = Path.Combine(root, "artifacts", "unit-tests.json");
File.WriteAllText(report, JsonSerializer.Serialize(new { passed = failures == 0, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} tests passed");
return failures == 0 ? 0 : 1;
