using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed record PdfWorkerResponse(ImportExtraction? Content, string? Error);
internal static class PdfImportWorker
{
    internal static ImportExtraction Extract(byte[] bytes, CancellationToken cancellation) => ExtractBounded(bytes, cancellation, TimeSpan.FromSeconds(30));
    internal static ImportExtraction ExtractBounded(byte[] bytes, CancellationToken cancellation, TimeSpan timeout, Action<int>? started = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var folder = LocalBackup.Root(Path.Combine(Path.GetTempPath(), "SecondBrain-pdf-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder); var input = Path.Combine(folder, "source.pdf"); var output = Path.Combine(folder, "result.json");
        try
        {
            File.WriteAllBytes(input, bytes);
            using var job = CreateJob();
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            foreach (var argument in new[] { "--extract-pdf", input, output }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("PDF worker could not start.");
            try
            {
                if (!AssignProcessToJobObject(job, process.Handle)) throw new IOException("PDF worker isolation is unavailable. No import was published.");
                // Parsing starts only after memory/lifetime limits apply. Parent death before this closes stdin.
                process.StandardInput.WriteLine("extract"); process.StandardInput.Close();
                started?.Invoke(process.Id);
                var watch = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (watch.Elapsed > timeout) throw new IOException("PDF extraction timed out. Split or re-export the document and retry.");
                    process.WaitForExit(50);
                }
                cancellation.ThrowIfCancellationRequested();
                if (!File.Exists(output) || new FileInfo(output).Length > 4_000_000) throw new IOException("PDF worker stopped without a readable result, possibly at its memory limit. The source is unchanged.");
                var result = JsonSerializer.Deserialize<PdfWorkerResponse>(File.ReadAllText(output)) ?? throw new IOException("PDF worker returned no result.");
                if (result.Error is { } error) throw new InvalidDataException(error);
                if (process.ExitCode != 0 || result.Content is not { Passages.Length: > 0, Warnings: not null } content
                    || content.Passages.Any(p => p.Text is null || p.Location is null) || content.Passages.Sum(p => p.Text.Length) > 500_000)
                    throw new InvalidDataException("PDF extraction returned invalid content.");
                return content;
            }
            finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
        }
        finally
        {
            // Only known files in our unique local temporary directory; never recursive cleanup.
            foreach (var file in new[] { input, output })
                try { LocalBackup.Root(file); if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            try { Directory.Delete(folder); } catch (IOException) { }
        }
    }
    internal static int Run(string input, string output)
    {
        try
        {
            if (Console.ReadLine() != "extract") return 2;
            PdfWorkerResponse response;
            try
            {
                input = LocalBackup.Root(input); output = LocalBackup.Root(output);
                if (new FileInfo(input).Length > 20_000_000) throw new InvalidDataException("PDF imports are limited to 20 MB.");
                response = new(PdfText.Read(File.ReadAllBytes(input)), null);
            }
            catch (Exception ex) { response = new(null, ex is InvalidDataException ? ex.Message : "PDF could not be read. Its source is unchanged."); }
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, response); file.Flush(true); return response.Error is null ? 0 : 2;
        }
        catch { return 2; }
    }
    private static SafeFileHandle CreateJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("PDF worker isolation could not be created."); }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 | 0x100 | 0x8, ActiveProcesses = 1 }, ProcessMemory = (UIntPtr)(512UL * 1024 * 1024) };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { handle.Dispose(); throw new IOException("PDF worker limits could not be applied."); }
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
