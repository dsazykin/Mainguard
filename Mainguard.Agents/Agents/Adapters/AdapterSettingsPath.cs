using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>
/// Which of the jail's two ephemeral trees a declared settings file lives under. Both die with the
/// jail, which is why either has to be persisted at all:
/// <list type="bullet">
///   <item><see cref="Home"/> — <c>/home/agent</c>, a 256 MiB <b>tmpfs</b> mounted over the image's
///   home. Where a CLI keeps its USER-level configuration (claude-code:
///   <c>~/.claude/settings.json</c>).</item>
///   <item><see cref="Workspace"/> — <c>/workspace</c>, the bind mount of this agent's git worktree,
///   which the daemon creates per spawn and deletes at teardown. Where a CLI keeps its PROJECT-level
///   configuration, and — the reason this root exists at all — where claude-code writes the grants a
///   user creates by answering "yes, and don't ask again" (<c>.claude/settings.local.json</c>). A
///   home-only design would have persisted a file the CLI never writes and missed every approval the
///   owner actually made.</item>
/// </list>
/// </summary>
public enum AdapterSettingsRoot
{
    /// <summary>Relative to the agent's <c>$HOME</c> (<c>ContainerSpecBuilder.AgentHome</c>).</summary>
    Home,

    /// <summary>Relative to the agent's checkout (<c>ContainerSpecBuilder.WorkspaceTarget</c>).</summary>
    Workspace,
}

/// <summary>
/// One file a CLI keeps its NON-CREDENTIAL configuration in — the permission allowlist above all —
/// declared by the adapter so Mainguard restores it into every trusted jail and harvests it back.
///
/// <para>Deliberately a separate declaration from <see cref="AdapterSpec.CredentialPaths"/>, and
/// <see cref="AdapterManifest.Parse"/> refuses any overlap between the two lists. The stores differ
/// in kind: a credential goes to the host OS keychain and nowhere else (the owner's standing rule),
/// while settings go to an ordinary per-repo JSON file the owner can read, edit and delete. Letting
/// one path appear in both lists would quietly divert a credential into the plaintext store.</para>
/// </summary>
/// <param name="Root">Which ephemeral tree <paramref name="Path"/> is relative to.</param>
/// <param name="Path">A plain relative file path (no leading <c>/</c> or <c>~</c>, no <c>..</c>),
/// validated by <see cref="AdapterManifest.IsHomeRelativeFilePath"/> — the same single gate every
/// other in-jail path this repo builds from a manifest goes through.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdapterSettingsPath(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("path")] string Path)
{
    /// <summary>The wire spellings of <see cref="AdapterSettingsRoot"/>. Ordinal and exact — a fuzzy
    /// match here would let a typo resolve to the wrong tree and write into the user's checkout.</summary>
    private static readonly IReadOnlyDictionary<string, AdapterSettingsRoot> RootNames =
        new Dictionary<string, AdapterSettingsRoot>(StringComparer.Ordinal)
        {
            ["home"] = AdapterSettingsRoot.Home,
            ["workspace"] = AdapterSettingsRoot.Workspace,
        };

    /// <summary>Every accepted <c>root</c> spelling, for the refusal messages.</summary>
    public static IReadOnlyCollection<string> RootSpellings => (IReadOnlyCollection<string>)RootNames.Keys;

    /// <summary>The wire spelling of a root — the inverse of <see cref="TryParseRoot"/>.</summary>
    public static string SpellRoot(AdapterSettingsRoot root) =>
        root == AdapterSettingsRoot.Workspace ? "workspace" : "home";

    /// <summary>Maps a declared <c>root</c> string to its tree. False for anything unrecognised — every
    /// caller refuses rather than defaulting, because a default would silently pick a tree.</summary>
    public static bool TryParseRoot(string? value, out AdapterSettingsRoot root)
    {
        root = AdapterSettingsRoot.Home;
        return value is not null && RootNames.TryGetValue(value, out root);
    }

    /// <summary>This entry's parsed root. Only reached after <see cref="AdapterManifest.Parse"/> (or
    /// <see cref="IsWellFormed"/>) accepted it, so an unrecognised value here is a bug — and it resolves
    /// to <see cref="AdapterSettingsRoot.Home"/>, the tmpfs that dies with the jail, never to the
    /// workspace (which is the user's real checkout).</summary>
    [JsonIgnore]
    public AdapterSettingsRoot ParsedRoot => TryParseRoot(Root, out var root) ? root : AdapterSettingsRoot.Home;

    /// <summary>The single shape gate, applied identically by the manifest parser, the spawn-side
    /// filter and the harvest side — so no leg can invent looser rules of its own.</summary>
    public bool IsWellFormed() =>
        TryParseRoot(Root, out _) && AdapterManifest.IsHomeRelativeFilePath(Path);
}

/// <summary>The limits the settings round-trip is held to, named once so the daemon and the tests
/// cannot disagree about them.</summary>
public static class AdapterSettingsPolicy
{
    /// <summary>
    /// The largest a single settings file may be to be harvested out of a jail (256 KiB).
    ///
    /// <para>A permission allowlist is a few kilobytes of JSON. The cap is not about disk: the jail's
    /// occupant can WRITE these files, so without a ceiling an agent could push arbitrary volume into a
    /// host-side store that is then re-injected into every later jail for that repository. Exceeding it
    /// is a refusal for that file, never a failed stop.</para>
    /// </summary>
    public const int MaxFileBytes = 256 * 1024;
}
