namespace SecondBrain.Core;

public sealed class DiagnosticLog(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "logs", "app.log");
    private readonly object gate = new();

    // Diagnostics must not crash an otherwise usable reader.
    public bool Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".previous", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
