using System.IO;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class AssistantSettings(string directory)
{
    private readonly string path = Path.Combine(directory, "assistant-settings.json");
    public AssistantOptions Load()
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<AssistantOptions>(File.ReadAllText(path));
        return value is { IsValid: true } ? value : throw new InvalidDataException("Invalid AI settings. Check assistant-settings.json.");
    }
    public void Save(AssistantOptions options)
    {
        if (!options.IsValid) throw new InvalidOperationException("Check model names, reasoning settings and context length.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
