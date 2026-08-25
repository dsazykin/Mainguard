using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The automated palette gate docs/design/DesignSystem.md §1.3/§3.2 called for but never had: every
/// theme file is parsed as text (no Avalonia — the tokens ARE the contract) and checked against the
/// design system's contrast policy. This locks in the 4-theme restyle: a retune that regresses text
/// legibility, lane distinguishability (incl. under deuteranopia), or the per-theme terminal ramps
/// (the Daylight dark-ramp-on-paper bug) fails here instead of in someone's eyes.
/// Thresholds are named constants — a deliberate policy change is a one-line, reviewable edit.
/// </summary>
public class ThemeContrastGateTests
{
    // WCAG AA for normal text.
    private const double TextContrastMin = 4.5;
    // Non-text graphics floor for commit-graph lanes against the panel they draw on.
    private const double LaneContrastMin = 3.2;
    // Pairwise CIE-L* (normalized 0..1) gap between lanes after deuteranopia simulation
    // (Viénot 1999) — the docs/design/DesignSystem.md G4 "deuteranopic-lightness" gate.
    private const double LaneDeutanGapMin = 0.070;
    // The terminal is a reading surface: strong default contrast, and every chromatic ANSI color
    // (1-6, 9-14; 0/7/8/15 are the conventional black/white/grey poles) must stay legible.
    private const double TerminalDefaultContrastMin = 7.0;
    private const double TerminalAnsiContrastMin = 2.5;

    private static readonly string[] SurfaceKeys =
        { "SurfaceWindow", "SurfacePanel", "SurfaceDeep", "SurfaceCard", "SurfaceHover" };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, Rgba>>> ThemesLazy =
        new(LoadThemes);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Rgba>> Themes => ThemesLazy.Value;

    public static TheoryData<string> ThemeNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Themes.Keys) data.Add(name);
        return data;
    }

    [Fact]
    public void EveryTheme_DefinesTheIdenticalKeySet()
    {
        Assert.True(Themes.Count >= 2, "expected at least two theme files");
        var reference = Themes.First();
        foreach (var (name, tokens) in Themes.Skip(1))
        {
            var missing = reference.Value.Keys.Except(tokens.Keys).ToList();
            var extra = tokens.Keys.Except(reference.Value.Keys).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"{name} vs {reference.Key}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}] " +
                "— a token missing from one theme is a runtime bug the compiler cannot catch");
        }
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void TextPrimary_MeetsAA_OnEverySurface_AndOnTheSelectionComposite(string theme)
    {
        var t = Themes[theme];
        var text = t["TextPrimary"];
        foreach (var surface in SurfaceKeys)
            AssertContrast(theme, $"TextPrimary on {surface}", text, t[surface], TextContrastMin);

        // Selected row: AccentSelection is a translucent tint composited over the card it sits on.
        var selected = t["AccentSelection"].Over(t["SurfaceCard"]);
        AssertContrast(theme, "TextPrimary on AccentSelection∘SurfaceCard", text, selected, TextContrastMin);
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void TextMuted_MeetsAA_OnThePanel(string theme)
    {
        var t = Themes[theme];
        AssertContrast(theme, "TextMuted on SurfacePanel", t["TextMuted"], t["SurfacePanel"], TextContrastMin);
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void OnAccent_MeetsAA_OnEveryFilledButtonBrush(string theme)
    {
        var t = Themes[theme];
        var onAccent = t["OnAccent"];
        AssertContrast(theme, "OnAccent on AccentBrush", onAccent, t["AccentBrush"], TextContrastMin);
        AssertContrast(theme, "OnAccent on SuccessBrush", onAccent, t["SuccessBrush"], TextContrastMin);
        AssertContrast(theme, "OnAccent on DangerBrush", onAccent, t["DangerBrush"], TextContrastMin);
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void Lanes_ClearThePanel_AndStayDistinct_UnderDeuteranopia(string theme)
    {
        var t = Themes[theme];
        var panel = t["SurfacePanel"];
        var lanes = Enumerable.Range(1, 5).Select(i => (Name: $"Lane{i}", Color: t[$"Lane{i}"])).ToList();

        foreach (var (name, color) in lanes)
            AssertContrast(theme, $"{name} on SurfacePanel", color, panel, LaneContrastMin);

        for (int i = 0; i < lanes.Count; i++)
        {
            for (int j = i + 1; j < lanes.Count; j++)
            {
                var gap = Math.Abs(lanes[i].Color.DeutanLightness() - lanes[j].Color.DeutanLightness());
                Assert.True(gap >= LaneDeutanGapMin,
                    $"{theme}: {lanes[i].Name} vs {lanes[j].Name} deuteranopic-lightness gap {gap:0.000} < {LaneDeutanGapMin} " +
                    "— these two lanes collapse for a deuteranopic reader");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void Terminal_DefaultTextIsStrong_AndEveryChromaticAnsiColorIsLegible(string theme)
    {
        var t = Themes[theme];
        var bg = t["TerminalBackground"];
        AssertContrast(theme, "TerminalForeground on TerminalBackground",
            t["TerminalForeground"], bg, TerminalDefaultContrastMin);

        foreach (var i in new[] { 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14 })
            AssertContrast(theme, $"TerminalAnsi{i} on TerminalBackground", t[$"TerminalAnsi{i}"], bg, TerminalAnsiContrastMin);
    }

    private static void AssertContrast(string theme, string what, Rgba fg, Rgba bg, double min)
    {
        var ratio = Rgba.ContrastRatio(fg.Over(bg), bg);
        Assert.True(ratio >= min, $"{theme}: {what} = {ratio:0.00}:1, needs ≥ {min}:1");
    }

    // ---- theme-file parsing ------------------------------------------------------------------

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Rgba>> LoadThemes()
    {
        var dir = Path.Combine(RepoRoot(), "Mainguard.UI", "Themes");
        var pattern = new Regex("x:Key=\"(?<key>[A-Za-z0-9]+)\"\\s+Color=\"#(?<hex>[0-9A-Fa-f]{6,8})\"");
        var themes = new Dictionary<string, IReadOnlyDictionary<string, Rgba>>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.axaml").OrderBy(f => f))
        {
            var tokens = new Dictionary<string, Rgba>();
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
                tokens[m.Groups["key"].Value] = Rgba.Parse(m.Groups["hex"].Value);
            Assert.True(tokens.Count > 0, $"no color tokens parsed from {file}");
            themes[Path.GetFileNameWithoutExtension(file)] = tokens;
        }
        return themes;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mainguard.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ---- color math --------------------------------------------------------------------------

    private readonly record struct Rgba(double A, double R, double G, double B)
    {
        public static Rgba Parse(string hex)
        {
            if (hex.Length == 6) hex = "FF" + hex;
            return new Rgba(
                Convert.ToInt32(hex[..2], 16) / 255.0,
                Convert.ToInt32(hex[2..4], 16) / 255.0,
                Convert.ToInt32(hex[4..6], 16) / 255.0,
                Convert.ToInt32(hex[6..8], 16) / 255.0);
        }

        /// <summary>Source-over composite of this (possibly translucent) color onto an opaque base.</summary>
        public Rgba Over(Rgba bg) => new(
            1.0,
            R * A + bg.R * (1 - A),
            G * A + bg.G * (1 - A),
            B * A + bg.B * (1 - A));

        public double Luminance()
        {
            static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }

        public static double ContrastRatio(Rgba a, Rgba b)
        {
            double la = a.Luminance(), lb = b.Luminance();
            var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
            return (hi + 0.05) / (lo + 0.05);
        }

        /// <summary>Normalized CIE L* (0..1) after Viénot (1999) deuteranopia simulation — the
        /// unnormalized Smith-Pokorny LMS matrices the paper's coefficients belong to.</summary>
        public double DeutanLightness()
        {
            static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            double r = Lin(R), g = Lin(G), b = Lin(B);

            double l = 17.8824 * r + 43.5161 * g + 4.11935 * b;
            double m = 3.45565 * r + 27.1554 * g + 3.86714 * b;
            double s = 0.0299566 * r + 0.184309 * g + 1.46709 * b;

            // Deuteranope: the M response is re-derived from L and S (the missing cone's plane).
            m = 0.494207 * l + 1.24827 * s;

            double r2 = Math.Clamp(0.0809444479 * l - 0.130504409 * m + 0.116721066 * s, 0, 1);
            double g2 = Math.Clamp(-0.0102485335 * l + 0.0540193266 * m - 0.113614708 * s, 0, 1);
            double b2 = Math.Clamp(-0.000365296938 * l - 0.00412161469 * m + 0.693511405 * s, 0, 1);

            double y = 0.2126 * r2 + 0.7152 * g2 + 0.0722 * b2;
            double f = y > 0.008856 ? Math.Cbrt(y) : (903.3 * y + 16) / 116;
            return (116 * f - 16) / 100.0;
        }
    }
}
