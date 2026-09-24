using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Input;

namespace SecondBrain.App;

internal enum CompanionCommand { ToggleControls, ToggleReaders, PreviousAnswer, NextAnswer, ResumeFollowing }
internal sealed record CommandShortcut(CompanionCommand Command, string Gesture);
internal sealed record ShortcutOptions
{
    public int SchemaVersion { get; init; } = 1;
    public CommandShortcut[] Bindings { get; init; } = [
        new(CompanionCommand.ToggleControls, "Ctrl+Alt+Space"), new(CompanionCommand.ToggleReaders, "Ctrl+Alt+R"),
        new(CompanionCommand.PreviousAnswer, "Ctrl+Alt+PageUp"), new(CompanionCommand.NextAnswer, "Ctrl+Alt+PageDown"),
        new(CompanionCommand.ResumeFollowing, "Ctrl+Alt+F")];
}
internal sealed class ShortcutSettings(string directory)
{
    private string PathName => Path.Combine(directory, "shortcuts.json");
    internal ShortcutOptions Load()
    {
        if (!File.Exists(PathName)) return new();
        var options = JsonSerializer.Deserialize<ShortcutOptions>(File.ReadAllText(PathName)) ?? throw new InvalidDataException("Empty shortcut settings.");
        GlobalShortcuts.Validate(options); return options;
    }
    internal void Save(ShortcutOptions options)
    {
        GlobalShortcuts.Validate(options); Directory.CreateDirectory(directory);
        File.WriteAllText(PathName + ".tmp", JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(PathName + ".tmp", PathName, true);
    }
}
internal interface IShortcutPlatform
{
    bool Register(IntPtr window, int id, uint modifiers, uint key);
    void Unregister(IntPtr window, int id);
}
internal sealed class WindowsShortcutPlatform : IShortcutPlatform
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    public bool Register(IntPtr window, int id, uint modifiers, uint key) => RegisterHotKey(window, id, modifiers, key);
    public void Unregister(IntPtr window, int id) => UnregisterHotKey(window, id);
}
internal sealed record ShortcutRegistration(CompanionCommand Command, string Gesture, bool Available, string Status);
internal sealed class GlobalShortcuts(IntPtr window, Action<CompanionCommand> execute, IShortcutPlatform platform) : IDisposable
{
    internal const int FirstId = 0x5100;
    private readonly Dictionary<int, CompanionCommand> active = [];
    private bool disposed;
    internal static KeyGesture? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > 80) throw new InvalidDataException("Shortcut is too long.");
        KeyGesture gesture;
        try { gesture = (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(text.Trim())!; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        { throw new InvalidDataException("Use a shortcut such as Ctrl+Alt+R, or leave it blank."); }
        if ((gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 || gesture.Modifiers.HasFlag(ModifierKeys.Windows) || gesture.Key is Key.None or Key.System || gesture.Key == Key.F12)
            throw new InvalidDataException("Include Ctrl or Alt. Windows-key combinations and F12 are reserved.");
        return gesture;
    }
    internal static void Validate(ShortcutOptions options)
    {
        if (options.SchemaVersion != 1 || options.Bindings is null || options.Bindings.Length != Enum.GetValues<CompanionCommand>().Length
            || options.Bindings.Any(b => b is null || b.Gesture is null || !Enum.IsDefined(b.Command)) || options.Bindings.Select(b => b.Command).Distinct().Count() != options.Bindings.Length)
            throw new InvalidDataException("Shortcut settings must contain each command once.");
        var used = new HashSet<(Key, ModifierKeys)>();
        foreach (var binding in options.Bindings)
            if (Parse(binding.Gesture) is { } key && !used.Add((key.Key, key.Modifiers))) throw new InvalidDataException("Two commands use the same shortcut. Choose a different combination.");
    }
    internal ShortcutRegistration[] Apply(ShortcutOptions options)
    {
        ObjectDisposedException.ThrowIf(disposed, this); Validate(options);
        Clear();
        return options.Bindings.Select(binding =>
        {
            var key = Parse(binding.Gesture);
            if (key is null) return new ShortcutRegistration(binding.Command, "", false, "Disabled");
            var id = FirstId + (int)binding.Command;
            if (!platform.Register(window, id, (uint)key.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key.Key)))
                return new ShortcutRegistration(binding.Command, binding.Gesture, false, "Unavailable: another app or Windows may be using this combination.");
            active.Add(id, binding.Command);
            return new ShortcutRegistration(binding.Command, binding.Gesture, true, "Ready");
        }).ToArray();
    }
    internal bool Handle(int message, IntPtr parameter)
    {
        if (disposed || message != 0x0312 || !active.TryGetValue(parameter.ToInt32(), out var command)) return false;
        execute(command); return true;
    }
    private void Clear() { foreach (var id in active.Keys) platform.Unregister(window, id); active.Clear(); }
    public void Dispose() { if (disposed) return; Clear(); disposed = true; }
}
