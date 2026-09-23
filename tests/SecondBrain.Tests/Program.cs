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
    Assert(new SettingsStore(path).Load(out var warning) == value && warning is null, "Round-trip mismatch");
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
        if (sprint > 0) Assert(status == "planned", "Future sprint unexpectedly marked implemented: " + id);
    }
    Assert(Enumerable.Range(0, 11).All(sprints.Contains), "A sprint has no requirements");
});
Test("Sprint 0 source has no audio, networking, recording or AI APIs", () =>
{
    var forbidden = new[] { "HttpClient", "WebSocket", "TcpClient", "UdpClient", "Socket(", "WebRequest", "NAudio", "Wasapi", "WaveIn", "Deepgram", "SpeechRecognizer", "Process.Start", "DllImport", "LibraryImport" };
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
    {
        var source = File.ReadAllText(file);
        foreach (var symbol in forbidden) Assert(!source.Contains(symbol, StringComparison.Ordinal), "Unexpected capability in " + file + ": " + symbol);
    }
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        Assert(!File.ReadAllText(file).Contains("PackageReference"), "Unexpected external runtime dependency");
});
var report = Path.Combine(root, "artifacts", "unit-tests.json");
File.WriteAllText(report, JsonSerializer.Serialize(new { passed = failures == 0, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} tests passed");
return failures == 0 ? 0 : 1;
