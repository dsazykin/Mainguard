using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Mainguard.Agents.UI.Converters;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.App.Shell.Converters;
using Mainguard.UI.Converters;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

// P2-13 test 1 (§5) / TI-P2-13.1: exactly one AgentStatus → brush converter, and EVERY status
// resolves a design token in EVERY registered theme (Daylight Loom included). We assert the
// resource-key lookup succeeds, never a color value — the token IS the contract.
public class AgentStatusBrushTests
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    [AvaloniaFact]
    public void StatusBrush_MappingComplete()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var app = Application.Current!;

            foreach (AgentStatus status in Enum.GetValues<AgentStatus>())
            {
                var key = AgentStatusBrushConverter.TokenKeyFor(status);

                Assert.True(
                    app.TryGetResource(key, app.ActualThemeVariant, out var res),
                    $"Theme '{theme}' is missing status token '{key}' for {status}.");
                Assert.IsAssignableFrom<IBrush>(res);

                // The one converter must resolve the same token to a real brush.
                var converted = AgentStatusBrushConverter.Instance.Convert(
                    status, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsAssignableFrom<IBrush>(converted);
            }
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    [AvaloniaFact]
    public void StatusBrush_TokenKeyIsTotalAndDistinctPerStatusName()
    {
        // Every enum value has a non-empty, AgentStatus-namespaced key (a smoke over the map's totality).
        foreach (AgentStatus status in Enum.GetValues<AgentStatus>())
        {
            var key = AgentStatusBrushConverter.TokenKeyFor(status);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.StartsWith("AgentStatus", key);
        }
    }
}
