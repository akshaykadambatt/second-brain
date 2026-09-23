using System.Text.Json;

namespace SecondBrain.Core;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width is >= 320 and <= 10000 && Height is >= 180 and <= 10000;
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool RememberReaderPosition { get; init; } = true;
    public WindowPlacement? ReaderPlacement { get; init; }
    public string ScriptText { get; init; } = ReaderSession.Sample;
    public ReaderStyle ReaderStyle { get; init; } = new();
    public List<PanelPlacement> Panels { get; init; } = [];
}

public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(directory, "settings.json");

    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                ?? throw new JsonException("Empty settings document.");
            if (result.SchemaVersion != 1) throw new JsonException("Unsupported settings version.");
            if (result.ReaderPlacement is { IsValid: false })
                throw new JsonException("Invalid reader position.");
            if (result.ScriptText is null || result.ScriptText.Length > 20000 || result.ReaderStyle is null
                || !result.ReaderStyle.IsValid || result.Panels is null
                || result.Panels.Any(p => p is null || !p.IsValid))
                throw new JsonException("Invalid reader settings.");
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warning = "Settings could not be loaded. Defaults are in use. " + ex.Message;
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        // Same-directory replace prevents partially written settings being published.
        File.Move(temp, FilePath, overwrite: true);
    }
}
