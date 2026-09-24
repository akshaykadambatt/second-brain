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

HistoryTests.Run(Test, Assert, Folder);
StorageTests.Run(Test, Assert, Folder);
SessionContextTests.Run(Test, Assert, Folder);
LatencyTests.Run(Test, Assert);
TranscriptDetailTests.Run(Test, Assert, Folder);
TranscriptReviewTests.Run(Test, Assert, Folder);
AudioSpeakerTests.Run(Test, Assert, Folder);

Test("Continuing a completed answer retains identities and sequence protection", () =>
{
    var inbox = new AnswerInbox(); var request = Guid.NewGuid(); var answer = inbox.Begin(request, Guid.NewGuid(), "Question");
    inbox.Accept(new(request, answer.Id, 0, "Existing grounded opening.", AnswerEventKind.Complete));
    var original = answer.Blocks[0];
    Assert(!inbox.Resume(Guid.NewGuid(), answer.Id) && inbox.Resume(request, answer.Id), "Resume did not validate request identity");
    Assert(!inbox.Resume(request, answer.Id), "Concurrent continuation accepted");
    Assert(inbox.Accept(new(request, answer.Id, 1, "Additional grounded detail.", AnswerEventKind.Complete)), "Next sequence was rejected");
    Assert(inbox.Answers.Count == 1 && ReferenceEquals(original, answer.Blocks[0]) && answer.Blocks.Count == 2, "Continuation replaced the answer or existing words");
    inbox.Resume(request, answer.Id); inbox.Supersede(request);
    Assert(!inbox.Resume(request, answer.Id) && !inbox.Accept(new(request, answer.Id, 2, "Late data")), "Canceled answer was revived");
});

Test("Appended meeting facts retain their own dates during filtered retrieval", () =>
{
    var vault = Folder("history-dates");
    File.WriteAllText(Path.Combine(vault, "Cedar.md"), "---\nproject: Cedar\ndate: 2026-01-01\n---\n# Cedar\nThe old budget was undecided.\n\n## Meeting update · 2026-09-24\nThe release is scheduled for Friday.\n");
    var index = new VaultIndex(); index.Rebuild(vault);
    var result = index.Search("release", new("Cedar", new DateOnly(2026, 9, 1)), new Dictionary<string, float[]>());
    Assert(result.Hits.Single().Chunk.Date == new DateOnly(2026, 9, 24), "New section inherited an old note date");
    Assert(index.Chunks.First().Date == new DateOnly(2026, 1, 1), "Earlier dated context was rewritten");
});

Test("Vault setup creates linked templates and preserves manual notes", () =>
{
    var root = Folder("vault-seed"); VaultFiles.Initialize(root);
    Assert(File.ReadAllText(Path.Combine(root, "Home.md")).Contains("[[Context/Role]]"), "Missing graph links");
    var role = Path.Combine(root, "Context", "Role.md"); File.WriteAllText(role, "Manual role content."); VaultFiles.Initialize(root);
    Assert(File.ReadAllText(role) == "Manual role content." && File.Exists(Path.Combine(root, "Templates", "Person.md")), "Setup overwrote manual content or missed a template");
    try { VaultFiles.SafePath(root, "../escape.md"); throw new Exception("Traversal accepted"); } catch (InvalidDataException) { }
});
Test("Vault manual edits, deletes and rebuilds update retrieval with project and date provenance", () =>
{
    var root = Folder("vault-index");
    File.WriteAllText(Path.Combine(root, "early.md"), "---\nproject: Cedar\ndate: 2026-01-01\n---\n# Deadline\nThe launch deadline is Friday.");
    File.WriteAllText(Path.Combine(root, "late.md"), "---\nproject: Cedar\ndate: 2026-09-24\n---\n# Deadline\nThe launch deadline is Monday.");
    File.WriteAllText(Path.Combine(root, "other.md"), "---\nproject: Pine\ndate: 2026-09-24\n---\n# Deadline\nThe deadline is Tuesday.");
    var index = new VaultIndex(); index.Rebuild(root);
    var hits = index.Search("launch deadline", new("Cedar"), new Dictionary<string, float[]>()).Hits;
    Assert(hits.Count == 2 && hits.All(h => h.Chunk.Project == "Cedar") && hits.Any(h => h.Chunk.Date == new DateOnly(2026, 1, 1)), "Project/date provenance lost");
    Assert(index.Search("deadline", new("Cedar", new DateOnly(2026, 9, 1)), new Dictionary<string, float[]>()).Hits.Single().Chunk.File == "late.md", "Date filter failed");
    File.WriteAllText(Path.Combine(root, "late.md"), "# Updated\nThe aurora release has no confirmed date."); File.Delete(Path.Combine(root, "early.md")); index.Rebuild(root);
    Assert(index.Search("aurora", new(), new Dictionary<string, float[]>()).Hits.Single().Chunk.File == "late.md" && index.Chunks.All(c => c.File != "early.md"), "Manual change/delete not indexed");
    var fresh = new VaultIndex(); fresh.Rebuild(root); Assert(fresh.Chunks.Select(c => c.Id).SequenceEqual(index.Chunks.Select(c => c.Id)), "Rebuild differs from source");
});
Test("Hybrid retrieval can recover a semantic paraphrase without lexical matches", () =>
{
    var root = Folder("vault-semantic"); File.WriteAllText(Path.Combine(root, "benefits.md"), "# Benefits\nEmployees receive twenty days of paid leave annually.");
    File.WriteAllText(Path.Combine(root, "network.md"), "# Network\nFirewall access requires security review.");
    var index = new VaultIndex(); index.Rebuild(root);
    var vectors = index.Chunks.ToDictionary(c => c.Id, c => c.File == "benefits.md" ? new float[] { 1, 0 } : [0, 1]);
    Assert(index.Search("vacation allowance", new(), vectors).Hits.Count == 0, "Fixture unexpectedly matches lexically");
    Assert(index.Search("vacation allowance", new(), vectors, [1, 0]).Hits.Single().Chunk.File == "benefits.md", "Semantic ranking failed");
    Assert(index.Search("zzunknown", new(), new Dictionary<string, float[]>()).Evidence.Contains("No relevant"), "Missing evidence not explicit");
    var longLine = VaultIndex.Parse("long.md", new string('a', 5000)); Assert(longLine.Select(c => c.Id).Distinct().Count() == longLine.Count && longLine.Sum(c => c.Text.Length) == 5000, "Long repeated line lost text or generated duplicate IDs");
});
Test("Meeting export preserves source, dated links and human edits without duplicate imports", () =>
{
    var root = Folder("meeting-vault"); VaultFiles.Initialize(root);
    using var session = new RecordingSession(Folder("meeting-source"), [new(AudioSource.Microphone, "fixture", "Synthetic", 16000), new(AudioSource.System, "output", "Synthetic output", 16000)], DateTimeOffset.Parse("2026-09-24T12:00:00Z"));
    using (var journal = new TranscriptLog(session.DirectoryPath, session.Manifest.Id))
    { journal.Append(new(session.Manifest.Id, "Final", AudioSource.Microphone, 1, 2, "We agreed the trial will be Friday.", Id: "one")); journal.Append(new(session.Manifest.Id, "Gap", AudioSource.System, 2, 3, "Connection gap.")); }
    session.Complete(3); var raw = File.ReadAllText(Path.Combine(session.DirectoryPath, "transcript.jsonl"));
    var summary = VaultFiles.ExportMeeting(root, session.DirectoryPath, "Cedar"); var full = VaultFiles.SafePath(root, summary);
    Assert(File.ReadAllText(full).Contains("#^t000000|source") && File.ReadAllText(full).Contains("2026-09-24"), "Summary provenance/date missing");
    Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(full)!, "transcript.jsonl")) == raw, "Original transcript changed");
    File.AppendAllText(full, "\nMy own note."); Assert(VaultFiles.ExportMeeting(root, session.DirectoryPath, "Cedar") == summary && File.ReadAllText(full).EndsWith("My own note."), "Repeated import duplicated or overwrote a note");
});

Test("Live questions combine final chunks and ignore changing provisional text", () =>
{
    var utterance = new QuestionUtterance();
    Assert(utterance.Observe(new(0, 1, "What is the", true, false, .99f), 0) is null, "Premature partial question");
    Assert(utterance.Observe(new(1, .5, "wrong draft", false, false, .5f), .5) is null && utterance.Flush(1) is null, "Provisional became a question or caused early flush");
    Assert(utterance.Observe(new(1, 1, "next milestone?", true, true, .99f), 1) == "What is the next milestone?", "Question lost a final segment");
    Assert(utterance.Flush(3) is null, "Completed question repeated");
    utterance.Observe(new(3, 1, "How does this work", true, false, .99f), 3);
    Assert(utterance.Flush(4.1) == "How does this work", "Missing endpoint cannot recover after quiet timeout");
});

Test("AI detector gates source, provisional speech, duplicate questions and generated echoes", () =>
{
    var entry = new TranscriptEntry(Guid.NewGuid(), "Final", AudioSource.System, 0, 1, "What should we do next?");
    Assert(QuestionGate.Detect(entry) == entry.Text, "System question missed");
    Assert(QuestionGate.Detect(entry with { Source = AudioSource.Microphone }) is null && QuestionGate.Detect(entry with { Kind = "Provisional" }) is null, "Wrong source triggered");
    var gate = new QuestionGate();
    Assert(gate.Accept(entry.Text, 0, true) && !gate.Accept("WHAT should we do next", 20, false), "Duplicate not suppressed");
    Assert(!gate.Accept("Why is this important?", 2, true) && gate.Accept("Why is this important?", 11, true), "Cooldown failed");
    gate.RememberAnswer("How can we improve reliability? We should verify the changes.");
    Assert(!gate.Accept("How can we improve reliability?", 30, true), "Generated echo triggered");
    gate.Forget(entry.Text); Assert(gate.Accept(entry.Text, 40, false), "Failed question cannot be retried");
});
Test("AI context bounds history, retains source and gap markers, and resets sessions", () =>
{
    var context = new AssistantContext(); var id = Guid.NewGuid(); var delivered = 0;
    context.Received += _ => delivered++;
    var item = new TranscriptEntry(id, "Final", AudioSource.Microphone, 1, 2, "A known fact.", Id: "one");
    context.Observe(item); context.Observe(item);
    Assert(delivered == 1 && context.Snapshot().Contains("Microphone"), "Duplicate or source failure");
    for (var i = 0; i < 150; i++) context.Observe(item with { Id = i.ToString(), Text = new string('a', 200) });
    Assert(context.Truncated && context.Snapshot().Length < 20000, "History unbounded");
    context.Observe(item with { SessionId = Guid.NewGuid(), Kind = "RunStart" });
    Assert(context.Snapshot() == "" && !context.Truncated, "Previous meeting context leaked");
    context.Observe(item with { SessionId = context.SessionId, Kind = "Gap", Text = "Network gap." });
    Assert(context.Snapshot().Contains("Gap"), "Gap missing");
});
Test("AI readable buffer withholds partial sentences and preserves complete blocks", () =>
{
    var buffer = new ReadableAnswerBuffer();
    Assert(buffer.Push("This is a partial").Count == 0, "Partial sentence displayed");
    Assert(buffer.Push(" sentence.\n\nNext").Single() == "This is a partial sentence.", "Readable block missing");
    Assert(buffer.Push(" paragraph.", true).Single() == "Next paragraph.", "Final paragraph missing");
    try { new ReadableAnswerBuffer().Push("Unfinished words", true); throw new Exception("Incomplete final accepted"); } catch (InvalidDataException) { }
});
Test("Responses events reject gaps, conflicts, failures and response identity changes", () =>
{
    var parser = new ResponsesEvents();
    var created = "{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"a\"}}";
    parser.Read(created); Assert(parser.Read(created) is null, "Duplicate created delivered");
    Assert(parser.Read("{\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"delta\":\"Hello.\"}")?.Text == "Hello.", "Delta missing");
    foreach (var bad in new[] { "{\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"delta\":\"Wrong.\"}", "{\"type\":\"response.completed\",\"sequence_number\":3}", "{\"type\":\"response.completed\",\"response\":{\"id\":\"b\"}}" })
        try { parser.Read(bad); throw new Exception("Invalid event accepted"); } catch (InvalidDataException) { }
    try { parser.Read("{\"type\":\"response.incomplete\"}"); throw new Exception("Incomplete response accepted"); } catch (InvalidOperationException) { }
    Assert(parser.Read("{\"type\":\"response.completed\",\"sequence_number\":2,\"response\":{\"id\":\"a\"}}")?.Complete == true, "Completion missing");
});

Test("Transcription maps sample time across idle output and reconnect epochs", () =>
{
    var map = new TranscriptTimeline(16000);
    map.Add(1, 16000); map.Add(2, 16000); map.Add(10, 16000);
    Assert(map.Map(1.5) == 2.5 && map.Map(2, true) == 3 && map.Map(2) == 10 && map.Map(2.5) == 10.5, "Idle output compressed the session clock or broke boundary mapping");
    var reconnect = new TranscriptTimeline(48000); reconnect.Add(30, 48000);
    Assert(reconnect.Map(.25) == 30.25, "Reconnect clock did not restart against its new source anchor");
    try { map.Map(500); throw new Exception("Invalid provider timestamp accepted"); } catch (InvalidDataException) { }
});
Test("Transcript journal deduplicates, preserves provenance and exports readable audio links", () =>
{
    var folder = Folder("transcripts"); var id = Guid.NewGuid();
    var entry = new TranscriptEntry(id, "Final", AudioSource.Microphone, 1.5, 2.5, "Deepgram and WASAPI", Guid.NewGuid(), "stable-id");
    using (var journal = new TranscriptLog(folder, id))
    {
        Assert(journal.Append(entry) && !journal.Append(entry), "Final replay duplicated a segment");
        journal.Append(new(id, "Gap", AudioSource.System, 2, 5, "Connection lost"));
        journal.ExportMarkdown();
        Assert(TranscriptLog.Read(folder).Length == 2 && File.ReadAllText(Path.Combine(folder, "transcript.md")).Contains("microphone.wav#t=1.500", StringComparison.Ordinal), "Missing provenance/audio link");
        try { using var collision = new TranscriptLog(folder, id); throw new Exception("Concurrent writer allowed"); } catch (IOException) { }
    }
    using var reopened = new TranscriptLog(folder, id);
    Assert(!reopened.Append(entry) && TranscriptLog.Read(folder)[0] == entry, "Restart lost final identity or source clocks");
});
Test("Interrupted transcript recovery preserves complete lines and marks unconfirmed tails", () =>
{
    var folder = Folder("transcript-recovery"); var id = Guid.NewGuid();
    using (var journal = new TranscriptLog(folder, id))
    {
        journal.Append(new(id, "RunStart", null, 0, 0, "Started"));
        journal.Append(new(id, "Final", AudioSource.System, 1, 2, "Saved", Id: "saved"));
    }
    File.AppendAllText(Path.Combine(folder, "transcript.jsonl"), "{\"partial\":");
    var manifest = new RecordingManifest { Id = id, DurationSeconds = 5 };
    TranscriptLog.Recover(folder, manifest); var recovered = TranscriptLog.Read(folder);
    Assert(recovered.Count(e => e.Kind == "Final") == 1 && recovered.Any(e => e.Kind == "Gap" && e.Source == AudioSource.System && e.Start == 2 && e.End == 5), "Saved words or explicit lost tail missing");
    TranscriptLog.Recover(folder, manifest);
    Assert(TranscriptLog.Read(folder).Length == recovered.Length, "Recovery duplicated records");
    File.AppendAllText(Path.Combine(folder, "transcript.jsonl"), "not-json\n");
    try { using var broken = new TranscriptLog(folder, id); throw new Exception("Corrupt complete record silently hidden"); } catch (JsonException) { }
});

Test("Voice acceptance replay covers silence, fillers, skips, jargon, repeats and source ownership", () =>
{
    var evidence = new List<object>();
    foreach (var trial in VoiceReplayCorpus.Trials)
    {
        var session = new ReaderSession(); session.Load(trial.Script); var flow = new VoiceFlow(session); flow.Start();
        var time = 0d; var lastCursor = 0d; var previous = 0;
        foreach (var item in trial.Events)
        {
            while (time + 1d / 120 < item.At)
            {
                flow.Tick(1d / 120); time += 1d / 120;
                Assert(flow.Cursor >= lastCursor, trial.Name + ": backward animation"); lastCursor = flow.Cursor;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew(); flow.Observe(item.Segment, item.Source);
            Assert(session.Position == item.Position, trial.Name + ": expected " + item.Position + " got " + session.Position);
            Assert(session.Position >= previous, trial.Name + ": backward match"); previous = session.Position;
            Assert(watch.Elapsed.TotalSeconds < 2, "Matcher missed the two-second reacquisition bound");
            evidence.Add(new { trial.Name, item.At, item.Source, session.Position, flow.Cursor, flow.WordsPerMinute, flow.LastMatchMilliseconds });
        }
        for (var i = 0; i < 240; i++) flow.Tick(1d / 120);
        var held = flow.Cursor; flow.Tick(10);
        Assert(flow.Holding && flow.Cursor == held, trial.Name + ": silence drift after hold");
    }
    File.WriteAllText(Path.Combine(root, "artifacts", "voice-corpus.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
});
Test("Voice pace uses stable progress; uncertainty and stalls cannot cause catch-up", () =>
{
    var session = new ReaderSession(); session.Load(ReaderSession.Sample); var flow = new VoiceFlow(session); flow.Start();
    flow.Observe(new(0, 1, "today I want", true, true, .95f));
    for (var i = 0; i < 20; i++) flow.Tick(1d / 60);
    var pace = flow.WordsPerMinute;
    flow.Observe(new(1, .5, "to talk about", true, true, .95f));
    Assert(flow.WordsPerMinute > pace, "Stable faster speech did not influence pace");
    var before = flow.Cursor;
    flow.Tick(5);
    Assert(flow.Cursor == before && flow.Holding, "Rendering stall caused catch-up movement");
    var position = session.Position;
    flow.Observe(new(1.5, .5, "the weather is sunny", true, true, .95f));
    for (var i = 0; i < 90; i++) flow.Tick(1d / 60);
    Assert(session.Position == position && flow.Holding, "Off-script words advanced or restarted movement");
    flow.Stop(); flow.Observe(new(2, 1, "our next steps", true, true, .95f));
    Assert(session.Position == position, "Stopped controller accepted delayed speech");
});
Test("Multi-line accepted progress settles with bounded frame steps and no overshoot", () =>
{
    foreach (var hz in new[] { 30, 60, 120 })
    {
        var motion = new ReaderMotion(); motion.Reset(100); motion.Follow(325);
        for (var i = 0; i < hz * 6; i++)
        {
            var prior = motion.Position; motion.Step(i == 10 ? 5 : 1d / hz, 45);
            Assert(motion.Position >= prior && motion.Position <= 325 && motion.Position - prior <= 45 * 2.2 / 30 + .001, "Catch-up jumped, reversed or overshot its accepted line");
        }
        Assert(motion.Position == 325 && !motion.Moving, "Accepted multi-line progress was abandoned");
        var held = motion.Position; motion.Step(10, 45); Assert(motion.Position == held, "Continued beyond accepted line");
    }
});
Test("Manual recovery clears voice momentum and permits timed fallback", () =>
{
    var session = new ReaderSession(); session.Load(ReaderSession.Sample); var playback = new ReaderPlayback(session); playback.StartVoice();
    playback.Voice.Observe(new(0, 1, "today I want to talk", true, true, .95f)); playback.Tick(.03);
    session.Select(1); Assert(playback.Voice.Active && playback.Voice.Holding && playback.Cursor == 1 && playback.Voice.Cursor == 1, "Manual click lost voice tracking or retained old momentum");
    playback.Voice.Observe(new(0, 1, "today I want to talk", true, true, .95f));
    Assert(session.Position == 1, "Late finalized phrase moved the manual position");
    playback.Voice.Observe(new(1, 1, "I want to talk", true, true, .95f));
    Assert(session.Position == 5 && playback.Voice.Active, "Fresh speech did not follow from the manually selected word");
    playback.Play(); playback.Tick(.03); Assert(playback.TimedMode && playback.Playing && !playback.Voice.Active, "Timed fallback failed");
});
Test("Manual voice reposition rejects in-flight revisions but accepts the next phrase", () =>
{
    var session = new ReaderSession(); session.Load("alpha beta gamma delta epsilon zeta");
    var playback = new ReaderPlayback(session); playback.StartVoice();
    playback.Voice.Observe(new(0, 1, "alpha beta", false, false, .99f));
    session.Select(2); session.Select(2);
    playback.Voice.Observe(new(0, 1, "gamma delta", true, true, .99f));
    Assert(session.Position == 2 && playback.Voice.Active, "Old in-flight phrase defeated manual position");
    playback.Voice.Observe(new(1, 1, "gamma delta", true, true, .99f));
    Assert(session.Position == 4, "Fresh final needed an extra resume or sacrificial phrase");
    playback.Pause(); session.Select(0);
    playback.Voice.Observe(new(2, 1, "alpha beta", true, true, .99f));
    Assert(!playback.Voice.Active && session.Position == 0, "Navigation restarted explicitly paused voice");
    playback.StartVoice(); session.Select(2);
    playback.Voice.Observe(new(0, 1, "gamma delta", true, true, .99f));
    Assert(session.Position == 4, "Navigation before first speech discarded the first fresh phrase");
    session.Load("A different document stays paused.");
    Assert(!playback.Voice.Active && !playback.Playing, "Document replacement inherited active voice tracking");
});

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

Test("Audio sprint excludes recognition fallback and unrelated AI capabilities", () =>
{
    var forbidden = new[] { "SpeechRecognitionEngine", "DictationGrammar", "api.anthropic.com" };
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
Test("Subpixel motion tails finish exactly within the existing frame budget", () =>
{
    foreach (var hz in new[] { 40, 60, 120 })
    {
        var motion = new ReaderMotion(); motion.Reset(1.816666666666677); motion.Follow(46.61666666666669);
        for (var i = 0; i < hz * 2.2; i++)
        {
            var prior = motion.Position; motion.Step(1d / hz, 44.8);
            Assert(motion.Position >= prior && motion.Position - prior <= 44.8 * 2.2 / hz + .000001, "Tail exceeded frame speed cap");
        }
        Assert(motion.Position == motion.Target && !motion.Moving, "Subpixel tail remained active");
    }
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
Test("Streaming orders chunks, buffers paragraphs and ignores exact duplicates", () =>
{
    var inbox = new AnswerInbox(); var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "First");
    void Send(int n, string t, AnswerEventKind kind = AnswerEventKind.Delta) => inbox.Accept(new(answer.RequestId, answer.Id, n, t, kind));
    Send(1, "world.\n\nNext "); Assert(answer.Blocks.Count == 0, "Out-of-order content leaked");
    Send(0, "Hello "); Assert(answer.Blocks.Count == 1 && answer.Blocks[0].Text == "Hello world.", "Paragraph buffering failed");
    var block = answer.Blocks[0]; Send(1, "world.\n\nNext ");
    Send(2, "paragraph.", AnswerEventKind.Complete);
    Assert(answer.Blocks.Count == 2 && answer.WordCount == 4 && answer.Blocks[1].Text == "Next paragraph.", "Duplicate or final flush corrupted text");
    Assert(ReferenceEquals(block, answer.Blocks[0]) && block.Words[0].BlockId == block.Id, "Published block changed identity");
});
Test("Superseded, wrong-request and interrupted responses retain stable content", () =>
{
    var inbox = new AnswerInbox(); var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "First");
    Assert(!inbox.Accept(new(Guid.NewGuid(), answer.Id, 0, "Wrong\n\n")), "Wrong request accepted");
    inbox.Accept(new(answer.RequestId, answer.Id, 0, "Keep this.\n\nUnfinished"));
    inbox.Supersede(answer.RequestId);
    Assert(!inbox.Accept(new(answer.RequestId, answer.Id, 1, " late", AnswerEventKind.Complete)), "Late superseded result accepted");
    Assert(answer.State == AnswerState.Superseded && answer.Blocks.Single().Text == "Keep this.", "Supersede mutated displayed text");
    var interrupted = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Interrupted");
    inbox.Accept(new(interrupted.RequestId, interrupted.Id, 0, "Stable.\n\nHalf", AnswerEventKind.Interrupted));
    Assert(interrupted.Blocks.Count == 1 && interrupted.State == AnswerState.Interrupted, "Incomplete interrupted paragraph displayed");
});
Test("Conflicting duplicates and unbounded ordering fail visibly without rewriting", () =>
{
    var inbox = new AnswerInbox(); var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Conflict");
    inbox.Accept(new(answer.RequestId, answer.Id, 0, "Original.\n\n"));
    inbox.Accept(new(answer.RequestId, answer.Id, 0, "Replacement.\n\n"));
    Assert(answer.State == AnswerState.Failed && answer.Blocks.Single().Text == "Original.", "Conflict rewrote original");
    var gap = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Gap");
    Assert(!inbox.Accept(new(gap.RequestId, gap.Id, 65, "Too far")) && gap.State == AnswerState.Failed, "Unlimited reorder buffer");
    var large = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Large");
    Assert(!inbox.Accept(new(large.RequestId, large.Id, 0, new string('x', 4097))) && large.State == AnswerState.Failed, "Oversized chunk accepted");
});
Test("Active answer appends preserve word IDs and queued responses cannot take over", () =>
{
    var inbox = new AnswerInbox(); var session = new ReaderSession(); inbox.Changed += session.RefreshAnswer;
    var first = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "First");
    inbox.Accept(new(first.RequestId, first.Id, 0, "One two three.\n\n")); session.ShowAnswer(first); session.Select(1);
    var words = session.Words.ToArray(); var document = session.DocumentId;
    var other = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Other"); inbox.Accept(new(other.RequestId, other.Id, 0, "Other answer.", AnswerEventKind.Complete));
    inbox.Accept(new(first.RequestId, first.Id, 1, "Four five.\n\n"));
    Assert(session.Position == 1 && session.DocumentId == document && words.Zip(session.Words).All(pair => ReferenceEquals(pair.First, pair.Second)), "Append reset anchor or word identity");
    Assert(session.Answer == first && session.Words.Count == 5, "Queued answer took over");
    first.SavedPosition = session.Position; session.ShowAnswer(other); session.ShowAnswer(first);
    Assert(session.Position == 1 && session.DocumentId == document, "Answer navigation lost position");
});
Test("Open streams wait at the end, resume gently and honor manual pauses", () =>
{
    var inbox = new AnswerInbox(); var session = new ReaderSession(); var playback = new ReaderPlayback(session);
    inbox.Changed += session.RefreshAnswer; var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Waiting"); session.ShowAnswer(answer);
    playback.Play(); Assert(playback.Waiting, "Empty open stream cannot wait");
    inbox.Accept(new(answer.RequestId, answer.Id, 0, "One two.\n\n"));
    for (var i = 0; i < 120; i++) playback.Tick(1d / 60);
    Assert(playback.Waiting && playback.Cursor == 2, "End did not wait");
    inbox.Accept(new(answer.RequestId, answer.Id, 1, "Three four.\n\n")); playback.Tick(1d / 60);
    Assert(playback.Playing && playback.Cursor > 2 && playback.Cursor < 2.01, "Resume reset or jumped");
    session.Select(0); inbox.Accept(new(answer.RequestId, answer.Id, 2, "Five six.\n\n")); playback.Tick(1);
    Assert(!playback.Playing && playback.Cursor == 0, "Append resumed after manual navigation");
    session.Select(session.Words.Count); playback.Play(); playback.Pause();
    inbox.Accept(new(answer.RequestId, answer.Id, 3, "Seven eight.", AnswerEventKind.Complete)); playback.Tick(1);
    Assert(!playback.Playing && playback.Cursor == 6, "Paused end resumed on completion");
});
Test("History is bounded explicitly and inactive answers never enter the active document", () =>
{
    var inbox = new AnswerInbox(); var session = new ReaderSession(); inbox.Changed += session.RefreshAnswer;
    for (var i = 0; i < AnswerInbox.Capacity; i++)
    {
        var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Answer " + i);
        inbox.Accept(new(answer.RequestId, answer.Id, 0, "One stable paragraph.", AnswerEventKind.Complete));
        if (i == 0) session.ShowAnswer(answer);
    }
    Assert(session.Words.Count == 3 && inbox.Answers.Count == 100, "History leaked into reader layout");
    try { inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Overflow"); throw new Exception("History overflow silently accepted"); }
    catch (InvalidOperationException ex) { Assert(ex.Message.Contains("100 answers"), "Capacity failure not explained"); }
});
Test("Dummy answer source exercises delayed, duplicate and interrupted events", () =>
{
    var inbox = new AnswerInbox(); var source = new ScriptedAnswerSource(inbox, 1);
    for (var i = 0; i < 300; i++) source.Tick(.05);
    Assert(!source.Running && inbox.Answers.Count == 3, "Demo did not finish");
    Assert(inbox.Answers[0].State == AnswerState.Complete && inbox.Answers[0].Blocks.Count == 4, "First answer corrupted by reordering");
    Assert(inbox.Answers[1].ParentAnswerId == inbox.Answers[0].Id && inbox.Answers[2].State == AnswerState.Interrupted, "Deeper answer or interruption missing");
});
Test("Streaming layout limits retain prior blocks and report oversized paragraphs", () =>
{
    var inbox = new AnswerInbox(); var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Limit");
    inbox.Accept(new(answer.RequestId, answer.Id, 0, "Keep this.\n\n"));
    Assert(!inbox.Accept(new(answer.RequestId, answer.Id, 1, new string('x', 1501))) && answer.State == AnswerState.Failed && answer.Blocks.Count == 1, "Oversized paragraph lost prior data or was not surfaced");
    var many = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Many blocks");
    for (var i = 0; i < AnswerInbox.MaximumBlocks; i++) inbox.Accept(new(many.RequestId, many.Id, i, "Short.\n\n"));
    Assert(!inbox.Accept(new(many.RequestId, many.Id, 128, "Excess.\n\n")) && many.Blocks.Count == 128 && many.State == AnswerState.Failed, "Unbounded block layout accepted");
});
Test("Microphone matcher continues into newly appended text without resetting the session", () =>
{
    var inbox = new AnswerInbox(); var session = new ReaderSession(); inbox.Changed += session.RefreshAnswer;
    var answer = inbox.Begin(Guid.NewGuid(), Guid.NewGuid(), "Speech"); session.ShowAnswer(answer);
    inbox.Accept(new(answer.RequestId, answer.Id, 0, "Hello reader.\n\n"));
    var progress = new DeepgramProgress(session);
    progress.Observe(new(0, 1, "Hello reader", true, true, .99f));
    Assert(session.Position == 2, "Speech did not reach current end");
    inbox.Accept(new(answer.RequestId, answer.Id, 1, "Welcome back.\n\n"));
    progress.Observe(new(2, 1, "Welcome back", true, true, .99f));
    Assert(session.Position == 4 && session.DocumentId == answer.Id, "Append broke microphone matching");
});
Test("Recording selections persist independently of voice-following selection", () =>
{
    var store = new SettingsStore(Folder("audio-settings"));
    store.Save(new() { MicrophoneId = "voice", RecordingMicrophoneId = "capture", RecordingOutputId = "speakers" });
    var value = store.Load(out _);
    Assert(value.MicrophoneId == "voice" && value.RecordingMicrophoneId == "capture" && value.RecordingOutputId == "speakers", "Recording device settings were lost");
});
Test("Recording preserves source clocks, pauses and playable PCM headers", () =>
{
    var tracks = new AudioTrack[] { new(AudioSource.Microphone, "mic", "Mic", 8000), new(AudioSource.System, "output", "Output", 16000) };
    using var recording = new RecordingSession(Folder("audio-clock"), tracks);
    foreach (var track in tracks)
    {
        var data = new byte[track.SampleRate * 2];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(track.SampleRate / 4 * 2), 12345);
        recording.Write(new(track.Source, 0, data));
    }
    recording.Mark("Paused", 1); recording.Mark("Recording", 2, "Resume");
    foreach (var track in tracks) recording.Write(new(track.Source, 2, new byte[track.SampleRate * 2], Silent: true));
    recording.Complete(3);
    foreach (var track in tracks)
    {
        var bytes = File.ReadAllBytes(Path.Combine(recording.DirectoryPath, track.Source.ToString().ToLowerInvariant() + ".wav"));
        Assert(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && BitConverter.ToInt32(bytes, 40) == track.SampleRate * 6, "Invalid WAV header or timeline duration");
        Assert(BitConverter.ToInt16(bytes, 44 + track.SampleRate / 4 * 2) == 12345, "Cross-source marker moved off the common clock");
        Assert(bytes.AsSpan(44 + track.SampleRate * 2, track.SampleRate * 2).ToArray().All(v => v == 0), "Pause was compressed instead of padded");
    }
    var manifest = RecordingSession.ReadManifest(recording.DirectoryPath);
    Assert(manifest.State == "Completed" && manifest.Chunks.Count == 4 && manifest.SilentFrames[AudioSource.Microphone] == 8000, "Chunk or captured-silence metadata missing");
    Assert(manifest.Events.Count(e => e.Kind == "NoCapturedPackets") == 2, "Uncaptured gaps were silently presented as recorded silence");
});
Test("Recovery rebuilds interrupted chunks and refuses an active session lock", () =>
{
    var tracks = new AudioTrack[] { new(AudioSource.Microphone, "mic", "Mic", 8000), new(AudioSource.System, "output", "Output", 8000) };
    string directory;
    using (var recording = new RecordingSession(Folder("audio-recovery"), tracks))
    {
        directory = recording.DirectoryPath; recording.Write(new(AudioSource.Microphone, 0, new byte[1600]));
        try { RecordingSession.Recover(directory); throw new Exception("Recovery entered a live session"); } catch (IOException) { }
    }
    var part = Directory.GetFiles(directory, "*.part").Single();
    using (var damaged = new FileStream(part, FileMode.Open, FileAccess.Write)) { damaged.Position = 40; damaged.Write(new byte[4]); damaged.Position = damaged.Length; damaged.WriteByte(99); }
    var recovered = RecordingSession.Recover(directory);
    Assert(recovered.State == "Recovered" && recovered.Chunks.Single().Frames == 800 && !Directory.GetFiles(directory, "*.part").Any(), "Incomplete header/tail was not repaired");
    Assert(File.Exists(Path.Combine(directory, "microphone.wav")) && File.Exists(Path.Combine(directory, "system.wav")), "Recovery did not produce both aligned tracks");
    Assert(RecordingSession.Recover(directory).Events.Count == recovered.Events.Count, "Repeated recovery duplicated events");
});
Test("Thirty-minute synthetic recording retains alignment and bounded recoverable chunks", () =>
{
    var tracks = new AudioTrack[] { new(AudioSource.Microphone, "mic", "Mic", 8000), new(AudioSource.System, "output", "Output", 16000) };
    using var recording = new RecordingSession(Folder("audio-thirty-minutes"), tracks);
    var packets = tracks.ToDictionary(t => t.Source, t => new byte[t.SampleRate * 20]);
    foreach (var track in tracks) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packets[track.Source], 14000);
    for (var second = 0; second < 1800; second += 10)
        foreach (var track in tracks) recording.Write(new(track.Source, second, packets[track.Source]));
    recording.Complete(1800);
    Assert(recording.Manifest.Chunks.Count == 360 && recording.Manifest.DurationSeconds == 1800, "Thirty-minute timeline or chunk count mismatch");
    foreach (var track in tracks)
    {
        using var file = File.OpenRead(Path.Combine(recording.DirectoryPath, track.Source.ToString().ToLowerInvariant() + ".wav"));
        Assert(file.Length == 44 + track.SampleRate * 2L * 1800, "Export duration changed with source sample rate");
        var marker = new byte[2];
        foreach (var second in new[] { 0, 600, 1200, 1790 })
        { file.Position = 44 + track.SampleRate * 2L * second; file.ReadExactly(marker); Assert(BitConverter.ToInt16(marker) == 14000, "Marker drifted at " + second); }
    }
    File.WriteAllText(Path.Combine(root, "artifacts", "audio-endurance.json"), JsonSerializer.Serialize(new { passed = true, simulatedSeconds = 1800, realTimeHardwareTest = false, recording.Manifest.DurationSeconds, chunks = recording.Manifest.Chunks.Count, markerAlignmentSamples = 0, sampleRates = tracks.Select(t => t.SampleRate) }, new JsonSerializerOptions { WriteIndented = true }));
});
var report = Path.Combine(root, "artifacts", "unit-tests.json");
File.WriteAllText(report, JsonSerializer.Serialize(new { passed = failures == 0, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} tests passed");
return failures == 0 ? 0 : 1;
