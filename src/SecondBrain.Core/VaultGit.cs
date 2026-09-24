using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core;

// A private object database and index: never touches an existing vault .git,
// user Git config, remotes, hooks, filters, or the application's repository.
public sealed class VaultGit
{
    public string Root { get; }
    public string DirectoryPath { get; }
    private readonly string executable;
    private readonly Dictionary<string, string> blobs = [];
    public VaultGit(string root, string? git = null)
    {
        Root = Path.GetFullPath(root);
        DirectoryPath = VaultFiles.SafePath(Root, ".secondbrain/history.git");
        executable = git ?? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"), "git" }.First(p => p == "git" || File.Exists(p));
    }
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DirectoryPath)!);
        if (!File.Exists(Path.Combine(DirectoryPath, "HEAD"))) Run(["init", "--bare", "--initial-branch=main", ".secondbrain/history.git"], repository: false);
    }
    public string Head => Run(["rev-parse", "--verify", "HEAD"], allowFailure: true).Trim();
    public string Prepare(IReadOnlyDictionary<string, byte[]> files, string message, string parent)
    {
        Run(["read-tree", "--empty"]);
        var index = new StringBuilder();
        foreach (var (path, bytes) in files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!blobs.TryGetValue(digest, out var blob)) blobs[digest] = blob = Run(["hash-object", "-w", "--stdin"], bytes).Trim();
            index.Append("100644 ").Append(blob).Append('\t').Append(path).Append('\0');
        }
        Run(["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(index.ToString()));
        var tree = Run(["write-tree"]).Trim();
        if (parent.Length > 0 && Run(["rev-parse", parent + "^{tree}"]).Trim() == tree) return parent;
        return Run(parent.Length == 0 ? ["commit-tree", tree, "-F", "-"] : ["commit-tree", tree, "-p", parent, "-F", "-"], Encoding.UTF8.GetBytes(message)).Trim();
    }
    public void Publish(string commit, string expected)
    { Run(["update-ref", "refs/heads/main", commit, expected.Length == 0 ? new string('0', 40) : expected]); }
    public string Checkpoint(IReadOnlyDictionary<string, byte[]> files, string message)
    { var parent = Head; var commit = Prepare(files, message, parent); if (commit != parent) Publish(commit, parent); return commit; }
    public string Log() => Head.Length == 0 ? "No saved history yet." : Run(["log", "-40", "--date=iso-strict", "--format=%h %ad %s"]);
    public string Diff(string commit)
    {
        if (!Regex.IsMatch(commit, "^[a-fA-F0-9]{7,40}$")) throw new InvalidDataException("Choose a saved revision.");
        return Run(["show", "--format=fuller", "--no-ext-diff", "--no-textconv", "--stat", "--patch", commit, "--", ":(exclude).secondbrain/**"]);
    }
    public string FindOperation(string operation) => Run(["log", "-1", "--format=%H", "--fixed-strings", "--grep=Operation: " + operation], allowFailure: true).Trim();
    private string Run(string[] args, byte[]? input = null, bool repository = true, bool allowFailure = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Root };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["GIT_AUTHOR_NAME"] = info.Environment["GIT_COMMITTER_NAME"] = "Second Brain";
        info.Environment["GIT_AUTHOR_EMAIL"] = info.Environment["GIT_COMMITTER_EMAIL"] = "local@secondbrain.invalid";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in new[] { "-c", "core.hooksPath=" + Path.Combine(DirectoryPath, "disabled-hooks"), "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false", "-c", "core.longpaths=true", "-c", "core.quotePath=false", "-c", "safe.directory=" + DirectoryPath.Replace('\\', '/') }) info.ArgumentList.Add(arg);
        // Git for Windows rejects a long absolute GIT_DIR before core.longpaths
        // can help. The working directory is already Root, so use relative paths.
        if (repository) { info.ArgumentList.Add("--git-dir=.secondbrain/history.git"); info.ArgumentList.Add("--work-tree=."); }
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Git could not start. Install Git for Windows to enable local history.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new IOException("Local Git operation timed out."); }
            Task.WaitAll(output, error);
            if (process.ExitCode != 0) { if (allowFailure) return ""; throw new IOException("Local Git operation failed (" + args[0] + "). History was not published; retry after checking vault access."); }
            return output.Result;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
