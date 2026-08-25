using Avalonia.Headless.XUnit;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

// CommandDeck and LoomAurora were retired in the 4-theme restyle. A persisted key from before the
// retirement must land on the nearest surviving relative (a deliberate mapping, not the silent
// default fallback) AND self-heal the store, while a genuinely unknown key keeps the old behavior:
// default theme, nothing persisted (so a newer build's key survives a downgrade round-trip).
public class ThemeRetirementMigrationTests
{
    [AvaloniaTheory]
    [InlineData("CommandDeck", "Graphite")]
    [InlineData("LoomAurora", "MidnightLoom")]
    public void Initialize_WithARetiredKey_AppliesTheReplacement_AndPersistsIt(string retired, string replacement)
    {
        string? persisted = null;
        var previousSeam = ThemeManager.PersistKey;
        try
        {
            ThemeManager.PersistKey = key => persisted = key;

            ThemeManager.Initialize(retired);

            Assert.Equal(replacement, ThemeManager.CurrentKey);
            Assert.Equal(replacement, persisted);
        }
        finally
        {
            ThemeManager.PersistKey = previousSeam;
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    [AvaloniaFact]
    public void Initialize_WithAnUnknownKey_FallsBackToDefault_WithoutPersisting()
    {
        string? persisted = null;
        var previousSeam = ThemeManager.PersistKey;
        try
        {
            ThemeManager.PersistKey = key => persisted = key;

            ThemeManager.Initialize("SomeFutureTheme");

            Assert.Equal(ThemeManager.DefaultKey, ThemeManager.CurrentKey);
            Assert.Null(persisted);
        }
        finally
        {
            ThemeManager.PersistKey = previousSeam;
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    [AvaloniaFact]
    public void RetiredKeys_AreNotInTheRegisteredLineup()
    {
        Assert.DoesNotContain(ThemeManager.Themes, t => t.Key == "CommandDeck");
        Assert.DoesNotContain(ThemeManager.Themes, t => t.Key == "LoomAurora");
        Assert.Contains(ThemeManager.Themes, t => t.Key == "Graphite");
    }
}
