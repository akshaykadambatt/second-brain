using System.IO;
using System.Windows;
using SecondBrain.Core;

namespace SecondBrain.App;

public partial class App : Application
{
    private DiagnosticLog? log;
    private FileStream? instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        string? smokePhase = null;
        string? importOpenAi = null;
        var setupVault = false;
        try
        {
            for (var i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) dataDirectory = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--smoke-test" && i + 1 < e.Args.Length) smokePhase = e.Args[++i];
                else if (e.Args[i] == "--import-openai-key" && i + 1 < e.Args.Length) importOpenAi = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--setup-vault") setupVault = true;
                else throw new ArgumentException("Expected --data-dir <directory> or --smoke-test <seed|verify|voice>.");
            }
            if (smokePhase is not null and not "seed" and not "verify" and not "voice" and not "deepgram" and not "flow" and not "timed" and not "study" and not "replay" and not "stream" and not "audio" and not "transcription" and not "companion" and not "companion-live" and not "knowledge" and not "knowledge-live" and not "history" and not "history-live" and not "storage" and not "wrap" and not "assistant" and not "assistant-live" and not "hybrid" and not "transcription-live" and not "performance" and not "performance30") throw new ArgumentException("Unknown smoke phase.");
            Directory.CreateDirectory(dataDirectory);
            if (importOpenAi is not null)
            {
                new ApiKeyStore(dataDirectory, "OpenAI").Import(importOpenAi);
                File.Delete(importOpenAi); Shutdown(0); return;
            }
            instanceLock = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (setupVault)
            {
                var vaultSettings = new VaultSettings(dataDirectory, AppContext.BaseDirectory);
                var options = vaultSettings.Load(); var root = vaultSettings.Resolve(options); VaultFiles.Initialize(root); vaultSettings.Save(options);
                var recordings = Path.Combine(dataDirectory, "recordings"); var imported = 0; var skipped = 0;
                if (Directory.Exists(recordings)) foreach (var folder in Directory.EnumerateDirectories(recordings))
                {
                    if (!File.Exists(Path.Combine(folder, "transcript.jsonl"))) continue;
                    try { VaultFiles.ExportMeeting(root, folder); imported++; } catch (Exception) { skipped++; }
                }
                var history = new VaultMaintenance(root);
                using (history.Acquire()) history.Initialize();
                File.WriteAllText(Path.Combine(dataDirectory, "vault-setup.json"), System.Text.Json.JsonSerializer.Serialize(new { root, imported, skipped, networkUsed = false, originalsPreserved = true, localHistory = history.Git.DirectoryPath }));
                Shutdown(0); return;
            }
            log = new DiagnosticLog(dataDirectory);
            var loggingAvailable = log.Write("Application started v0.13.1");
            var store = new SettingsStore(dataDirectory);
            var settings = store.Load(out var warning);
            var window = new MainWindow(store, settings, log, dataDirectory, hiddenTestMode: smokePhase is not null);
            MainWindow = window;
            window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width);
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height);
            if (warning is not null) window.SetStatus(warning, true);
            else if (!loggingAvailable) window.SetStatus("Diagnostic logging is unavailable. Check folder permissions.", true);
            DispatcherUnhandledException += (_, args) =>
            {
                log.Write("Unhandled error: " + args.Exception);
                if (smokePhase is null) MessageBox.Show("An unexpected error occurred. See the local diagnostic log.", "Second Brain");
                args.Handled = true;
                Shutdown(1);
            };
            window.Show();
            if (smokePhase == "performance") _ = PerformanceTest.Run(window, dataDirectory);
            else if (smokePhase == "performance30") _ = PerformanceTest.Run(window, dataDirectory, 30);
            else if (smokePhase is not null) _ = SmokeTest.Run(window, dataDirectory, smokePhase);
        }
        catch (Exception ex)
        {
            log?.Write("Startup failed: " + ex);
            if (smokePhase is null)
                MessageBox.Show("Second Brain could not start. Another copy may be using this data folder, or the folder may not be writable.\n\n" + ex.Message, "Second Brain");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        log?.Write("Application stopped");
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}
