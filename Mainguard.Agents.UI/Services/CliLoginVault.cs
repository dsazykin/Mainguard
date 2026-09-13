using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The host-side persistence format for a CLI's interactive-login state (P2-01 alignment: secrets
/// live ONLY in the OS-backed keyring — the agent side is tmpfs by design). One keyring entry per
/// adapter kind <b>and repository</b> (<c>cli_login_&lt;adapterId&gt;_&lt;repoScope&gt;</c>) holding a
/// JSON object of $HOME-relative path → base64 content, e.g.
/// <c>{".claude/.credentials.json": "eyJ..."}</c>. Pure (de)serialization — the keyring get/set stays
/// with the caller's injectable keystore funcs so tests never touch a real keyring.
///
/// <para><b>F2 — why the repo is in the key.</b> This entry used to be keyed by adapter kind ALONE
/// while the settings store beside it (<see cref="CliSettingsStore"/>) was already per repository.
/// That asymmetry was the cross-repo jump: a login harvested out of repository A's jail was restored
/// into every later jail of every OTHER repository of the same kind, so one repo's OAuth refresh
/// token — a credential that can mint fresh access tokens for the user's whole provider account —
/// reached code checked out from an unrelated repository. A permission allowlist was correctly judged
/// to be repo-scoped because it is a standing grant of execution; a refresh token is strictly more
/// than that, and it was the one that was shared.</para>
///
/// <para><b>Migration: none, by choice.</b> The legacy unscoped entry is never read again. A read
/// fallback to it would hand every repository the old shared blob, which is the defect restated, so
/// there is deliberately no fallback: the owner signs in once per repository, in that repository's
/// first attended jail, exactly as they did for their approved-command list. The stale
/// <c>cli_login_&lt;kind&gt;</c> entry is left in place rather than deleted — it is inert, and
/// destroying a credential on the user's behalf during an upgrade is not a thing an upgrade should
/// do. See <see cref="LegacyKeystoreKeyFor"/>.</para>
/// </summary>
public static class CliLoginVault
{
    /// <summary>The keyring entry prefix; the suffix is the adapter id plus the repository scope.</summary>
    public const string KeystoreKeyPrefix = "cli_login_";

    /// <summary>
    /// The keyring entry name for one adapter's saved login state <b>in one repository</b>, or
    /// <c>null</c> when either scope is blank.
    ///
    /// <para>A blank scope is not a wildcard: collapsing it into a shared bucket is precisely the
    /// defect this key shape exists to remove, so a caller that cannot name the repository stores and
    /// loads nothing (the CLI then asks for a login, which is the pre-vault behaviour).</para>
    /// </summary>
    public static string? KeystoreKeyFor(string? agentKind, string? repoHandle)
    {
        if (string.IsNullOrWhiteSpace(agentKind) || string.IsNullOrWhiteSpace(repoHandle))
        {
            return null;
        }

        return KeystoreKeyPrefix + CliSettingsStore.ScopeSegment(agentKind) + "_" + RepoScope(repoHandle);
    }

    /// <summary>
    /// The pre-F2 unscoped entry name. Kept ONLY so the shape has a name in tests and in the migration
    /// note — nothing reads its value any more, and nothing may start to: doing so would restore one
    /// repository's login into another's jail again.
    /// </summary>
    public static string LegacyKeystoreKeyFor(string agentKind) => KeystoreKeyPrefix + agentKind;

    /// <summary>
    /// The repository half of the key: <b>always</b> a 32-hex-character SHA-256 prefix, never the
    /// handle itself.
    ///
    /// <para>Unconditional hashing is what makes the key unambiguous. A keyring entry name is one flat
    /// string, and the characters <see cref="CliSettingsStore.ScopeSegment"/> lets through include
    /// <c>_</c> — so a variable-width repo segment would let kind <c>a_b</c> in repo <c>c</c> and kind
    /// <c>a</c> in repo <c>b_c</c> resolve to the same entry, which is the cross-repo read this change
    /// removes, reintroduced through the key parser. A fixed 32-character suffix cannot collide that
    /// way. It also keeps the opaque handle (a path on the owner's disk, in some deployments) out of a
    /// name that OS credential managers display.</para>
    /// </summary>
    private static string RepoScope(string repoHandle)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(repoHandle));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>Parses a stored vault value into login files. A null/blank/corrupt value (a hand-
    /// edited keyring file, an interrupted write) yields empty — the CLI just asks for a fresh
    /// login, which is the pre-vault behavior, never a crash.</summary>
    public static IReadOnlyList<CliLoginFile> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Array.Empty<CliLoginFile>();
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stored);
            if (map is null)
            {
                return Array.Empty<CliLoginFile>();
            }

            var files = new List<CliLoginFile>(map.Count);
            foreach (var (path, base64) in map)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(base64))
                {
                    continue;
                }

                try
                {
                    var content = Convert.FromBase64String(base64);
                    if (content.Length > 0)
                    {
                        files.Add(new CliLoginFile(path, content));
                    }
                }
                catch (FormatException)
                {
                    // One corrupt entry loses that file, not the whole vault.
                }
            }

            return files;
        }
        catch (JsonException)
        {
            return Array.Empty<CliLoginFile>();
        }
    }

    /// <summary>
    /// Serializes the vault after folding <paramref name="harvested"/> into <paramref name="stored"/>:
    /// a harvested path replaces its stored copy (the jail's version is always newer), while stored
    /// paths the harvest didn't return are KEPT — a file absent from one session (e.g. the CLI
    /// hadn't recreated it yet) must not erase a working login. Returns null when there is nothing
    /// to store (the caller skips the keyring write).
    /// </summary>
    public static string? MergeAndSerialize(string? stored, IReadOnlyList<CliLoginFile> harvested)
    {
        var merged = Parse(stored).ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        foreach (var file in harvested)
        {
            if (!string.IsNullOrWhiteSpace(file.Path) && file.Content is { Length: > 0 })
            {
                merged[file.Path] = file.Content;
            }
        }

        if (merged.Count == 0)
        {
            return null;
        }

        var map = merged.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value), StringComparer.Ordinal);
        return JsonSerializer.Serialize(map);
    }
}
