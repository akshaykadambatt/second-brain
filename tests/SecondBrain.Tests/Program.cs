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
    var value = new AppSettings { RememberReaderPosition = false, ReaderPlacement = new(100, 120, 520, 280) };
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
var report = Path.Combine(root, "artifacts", "unit-tests.json");
File.WriteAllText(report, JsonSerializer.Serialize(new { passed = failures == 0, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} tests passed");
return failures == 0 ? 0 : 1;
