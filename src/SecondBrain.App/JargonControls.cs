using SecondBrain.Core;

namespace SecondBrain.App;

public partial class MainWindow
{
    internal int JargonLevel { get; private set; } = SpokenLanguage.DefaultLevel;
    internal bool ChangeJargon(int direction)
    {
        var level = Math.Clamp(JargonLevel + Math.Sign(direction), 1, 5);
        if (level == JargonLevel) return false;
        try
        {
            // Update this preference only; do not silently save other in-progress settings edits.
            var saved = companionSettings!.Load(); companionSettings.Save(saved with { JargonLevel = level });
            JargonLevel = level;
            if (Companion is { } companion) companion.Answers.JargonLevelOverride = level;
            if (Assistant is { } assistant) assistant.Service.JargonLevelOverride = level;
            foreach (var panel in Panels) panel.SetJargon(level);
            return true;
        }
        catch (Exception)
        {
            const string error = "Jargon level could not be saved. The previous level is still active; check your settings folder.";
            foreach (var panel in Panels) panel.SetJargon(JargonLevel, error);
            SetStatus(error, true); return false;
        }
    }
}
