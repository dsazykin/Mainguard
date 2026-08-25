using Avalonia.Headless.XUnit;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

// The follow-the-OS theme mode: "System" is a MODE, never a member of ThemeManager.Themes (the
// render harnesses sweep that list — a pseudo entry would break every capture), it persists as
// its own key, resolves to a REAL theme, and an explicit theme choice exits the mode.
public class SystemThemeModeTests
{
    [AvaloniaFact]
    public void SystemKey_IsAMode_NotAThemeListEntry()
    {
        Assert.DoesNotContain(ThemeManager.Themes, t => t.Key == ThemeManager.SystemKey);
    }

    [AvaloniaFact]
    public void ApplySystem_ResolvesARealTheme_AndPersistsTheModeKey()
    {
        string? persisted = null;
        var previousSeam = ThemeManager.PersistKey;
        try
        {
            ThemeManager.PersistKey = key => persisted = key;

            ThemeManager.Apply(ThemeManager.SystemKey);

            Assert.True(ThemeManager.IsFollowingSystem);
            // CurrentKey is the RESOLVED theme (a real entry — the design system needs concrete
            // tokens); the persisted choice is the mode, so a restart re-enters it.
            Assert.Contains(ThemeManager.Themes, t => t.Key == ThemeManager.CurrentKey);
            Assert.Equal(ThemeManager.SystemKey, persisted);
        }
        finally
        {
            ThemeManager.PersistKey = previousSeam;
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    [AvaloniaFact]
    public void AnExplicitTheme_ExitsTheMode()
    {
        try
        {
            ThemeManager.Apply(ThemeManager.SystemKey, persist: false);
            Assert.True(ThemeManager.IsFollowingSystem);

            ThemeManager.Apply("Atelier", persist: false);

            Assert.False(ThemeManager.IsFollowingSystem);
            Assert.Equal("Atelier", ThemeManager.CurrentKey);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    [AvaloniaFact]
    public void Initialize_WithThePersistedModeKey_ReentersTheMode()
    {
        try
        {
            ThemeManager.Initialize(ThemeManager.SystemKey);

            Assert.True(ThemeManager.IsFollowingSystem);
            Assert.Contains(ThemeManager.Themes, t => t.Key == ThemeManager.CurrentKey);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }
}
