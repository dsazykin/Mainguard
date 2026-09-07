using System;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>
/// The limits and the shape rules the CLI <b>credential</b> round-trip is held to — the twin of
/// <see cref="AdapterSettingsPolicy"/>, named once so the daemon, the client and the tests cannot
/// disagree about them.
///
/// <para><b>F2.</b> The settings leg has had a size ceiling since it shipped; the credential leg had
/// none at all, and the two legs read files out of the SAME agent-writable tmpfs $HOME. A jail's
/// occupant could therefore write an arbitrarily large <c>.claude.json</c> and the daemon would base64
/// the whole thing over an exec pipe, into the daemon's memory, over gRPC, and into an OS keyring
/// entry — a store that has no business holding hundreds of megabytes and, on some backends, will
/// fail in ways the user experiences as "my login stopped working".</para>
/// </summary>
public static class AdapterCredentialPolicy
{
    /// <summary>
    /// The largest a single credential file may be to be harvested out of a jail (1 MiB), refused
    /// rather than truncated.
    ///
    /// <para><b>Why refuse and not truncate.</b> Half a JSON credential file is not a smaller
    /// credential file, it is a corrupt one — and it would REPLACE the good copy in the vault, because
    /// a harvested path always wins over its stored copy. A refusal keeps the last known-good login;
    /// a truncation destroys it and the user cannot tell why.</para>
    ///
    /// <para><b>Why 1 MiB and not the settings leg's 256 KiB.</b> These files are token blobs of a few
    /// kilobytes, with one exception: <c>.claude.json</c> also accumulates per-project state (prompt
    /// history, MCP entries) and grows with use. 256 KiB would start refusing real logins on long
    /// sessions, and a refusal here costs the user a sign-in. 1 MiB keeps roughly an order of
    /// magnitude of headroom over the largest observed real file while still bounding what a jail's
    /// occupant can push into a host-side credential store.</para>
    /// </summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// True when a declared credential path is really a CLI <b>settings</b> file wearing a credential
    /// path's clothes — today <c>.gemini/settings.json</c> and <c>.qwen/settings.json</c>.
    ///
    /// <para><b>Why this predicate exists instead of a manifest edit.</b> gemini-cli and qwen-code list
    /// their settings file under <c>credentialPaths</c>, which predates the <c>settingsPaths</c> field.
    /// The manifest comment is explicit that moving them would migrate live data out of the owner's
    /// keychain into a plaintext file — a migration, not a bug fix. So they stay where they are and the
    /// two protections the settings leg has are brought TO them instead: the grant scrub (an agent in
    /// an attended jail must not be able to persist a rule about Mainguard's own IPC mount just by
    /// writing it into a file the daemon files as a credential) and a size ceiling.</para>
    ///
    /// <para>Matched on the file NAME, not on a hardcoded pair of paths, so a sixth adapter that keeps
    /// its settings in <c>&lt;dir&gt;/settings.json</c> is covered the day it is added rather than the
    /// day somebody remembers this list. A false positive costs a scrub pass and a tighter cap on a
    /// file that was not a settings file, which is harmless; a false negative is the gap.</para>
    /// </summary>
    public static bool IsSettingsShaped(string? homeRelativePath) =>
        homeRelativePath is { Length: > 0 }
        && homeRelativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Equals("settings.json", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The ceiling that applies to one declared credential path: the settings leg's, for the
    /// settings files that sit in this field, and this leg's for everything else. A single place, so
    /// the harvest's in-shell check and the restore-side filter cannot drift apart.</summary>
    public static int MaxBytesFor(string? homeRelativePath) =>
        IsSettingsShaped(homeRelativePath) ? AdapterSettingsPolicy.MaxFileBytes : MaxFileBytes;

    /// <summary>The declared credential paths of one adapter that are settings-shaped — for the
    /// diagnostics and tests that want to name them.</summary>
    public static IReadOnlyList<string> SettingsShapedIn(IEnumerable<string>? credentialPaths) =>
        credentialPaths?.Where(IsSettingsShaped).ToArray() ?? Array.Empty<string>();
}
