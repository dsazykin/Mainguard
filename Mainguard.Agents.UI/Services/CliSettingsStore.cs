using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The host-side durable store for a CLI's SETTINGS — the permission allowlist above all — so the
/// approvals a user gives inside one jail are still there in the next one.
///
/// <para><b>Why this is not the keychain.</b> The standing rule is that logins live only in the host
/// OS keychain, and that rule is about <i>credentials</i>. Settings are not credentials: they are
/// configuration the owner has every reason to want to read, edit and throw away. So they go to an
/// ordinary JSON file under the Mainguard data root — the same root the daemon token, the adapter pins
/// and the app database already use — rather than into a keyring entry nobody can inspect. Keeping the
/// two stores distinct is enforced upstream too: <c>AdapterManifest.Parse</c> refuses a path declared
/// in both <c>credentialPaths</c> and <c>settingsPaths</c>, so a credential can never be diverted into
/// this plaintext file by a manifest edit.</para>
///
/// <para><b>Why the layout is per repository.</b> <c>&lt;data root&gt;/cli-settings/&lt;repo&gt;/&lt;adapter&gt;.json</c>.
/// A permission allowlist is a standing grant of execution, so its scope is a security decision, not a
/// storage convenience: approving <c>make deploy</c> while working on one repository must not silently
/// pre-approve whatever <c>make deploy</c> means in another. The repo handle is part of the PATH rather
/// than a key inside one shared file, so "forget everything this repo approved" is a directory the
/// owner can delete.</para>
///
/// <para>Pure file + (de)serialization, with no ambient knowledge of the jail's layout: an entry is a
/// (root, path) pair carrying base64 content, exactly as it crossed the wire.</para>
/// </summary>
public sealed class CliSettingsStore
{
    /// <summary>The directory under the Mainguard data root that holds every repo's settings.</summary>
    public const string DirectoryName = "cli-settings";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _root;

    /// <summary>The production store: <c>%LocalAppData%\Mainguard\cli-settings</c> on Windows,
    /// <c>~/.mainguard/cli-settings</c> elsewhere.</summary>
    public CliSettingsStore()
        : this(Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), DirectoryName))
    {
    }

    /// <summary>An explicit root — tests point this at a temp directory so nothing touches the
    /// user's real store.</summary>
    public CliSettingsStore(string root) =>
        _root = root ?? throw new ArgumentNullException(nameof(root));

    /// <summary>Where this repo + adapter's settings are kept. Public so a diagnostic (or the owner)
    /// can be told the exact file to look at or delete.</summary>
    public string FilePathFor(string repoHandle, string agentKind) =>
        Path.Combine(_root, ScopeSegment(repoHandle), ScopeSegment(agentKind) + ".json");

    /// <summary>
    /// The settings saved for this repository + CLI, or empty when there are none.
    ///
    /// <para>A missing, unreadable or corrupt file yields empty rather than throwing: the worst case
    /// is the pre-feature behaviour (the CLI asks about a command again), never a failed spawn.</para>
    /// </summary>
    public IReadOnlyList<CliSettingsFileEntry> Load(string repoHandle, string agentKind)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(agentKind))
        {
            // A blank scope is not a wildcard. Returning "whatever is stored" for an unknown repo is
            // precisely the cross-repo leak this store exists to prevent.
            return Array.Empty<CliSettingsFileEntry>();
        }

        try
        {
            var path = FilePathFor(repoHandle, agentKind);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Array.Empty<CliSettingsFileEntry>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<CliSettingsFileEntry>();
        }
    }

    /// <summary>
    /// Folds <paramref name="harvested"/> into this repository + CLI's stored settings and writes the
    /// result. A harvested entry replaces its stored copy (the jail's version is newer); stored entries
    /// the harvest did not return are KEPT, so a file the CLI had not recreated yet cannot erase a
    /// working allowlist. An empty harvest writes nothing at all.
    /// </summary>
    /// <returns>True when a file was written.</returns>
    public bool Save(string repoHandle, string agentKind, IReadOnlyList<CliSettingsFileEntry> harvested)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(agentKind)
            || harvested is not { Count: > 0 })
        {
            return false;
        }

        var merged = Load(repoHandle, agentKind).ToDictionary(KeyOf, e => e);
        foreach (var entry in harvested)
        {
            if (!string.IsNullOrWhiteSpace(entry.Root) && !string.IsNullOrWhiteSpace(entry.Path)
                && entry.Content is { Length: > 0 })
            {
                merged[KeyOf(entry)] = entry;
            }
        }

        if (merged.Count == 0)
        {
            return false;
        }

        var path = FilePathFor(repoHandle, agentKind);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(
                merged.Values
                    .OrderBy(e => e.Root, StringComparer.Ordinal)
                    .ThenBy(e => e.Path, StringComparer.Ordinal)
                    .Select(e => new StoredEntry(e.Root, e.Path, Convert.ToBase64String(e.Content)))
                    .ToArray(),
                WriteOptions);

            // Stage-then-replace: an interrupted write must not leave a half-parsed allowlist behind,
            // and the reader treats a corrupt file as "no settings" — which would silently drop every
            // approval the user had made.
            var staging = path + ".partial";
            File.WriteAllText(staging, json, Encoding.UTF8);
            File.Move(staging, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a settings write costs a re-approval next session; failing a stop would cost the
            // user their jail. Never the second.
            return false;
        }
    }

    /// <summary>Parses a store file's text. Public so the round-trip test can assert on the exact
    /// bytes that land on disk rather than on this class's in-memory state.</summary>
    public static IReadOnlyList<CliSettingsFileEntry> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Array.Empty<CliSettingsFileEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<StoredEntry[]>(stored);
            if (entries is null)
            {
                return Array.Empty<CliSettingsFileEntry>();
            }

            var result = new List<CliSettingsFileEntry>(entries.Length);
            foreach (var entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Root)
                    || string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Content))
                {
                    continue;
                }

                try
                {
                    var content = Convert.FromBase64String(entry.Content);
                    if (content.Length > 0)
                    {
                        result.Add(new CliSettingsFileEntry(entry.Root, entry.Path, content));
                    }
                }
                catch (FormatException)
                {
                    // One corrupt entry loses that file, not the whole allowlist.
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<CliSettingsFileEntry>();
        }
    }

    private static string KeyOf(CliSettingsFileEntry entry) => entry.Root + " " + entry.Path;

    /// <summary>
    /// One path segment for a scope value. A repo handle is an opaque daemon handle and an adapter id
    /// is manifest-controlled, but both become DIRECTORY NAMES here, so anything that is not plainly
    /// safe is replaced by a hash of the original rather than sanitised in place — two different
    /// scopes must never be able to collapse onto one directory, which is how a per-repo store quietly
    /// becomes a shared one.
    /// </summary>
    private static string ScopeSegment(string value)
    {
        var safe = value.Length is > 0 and <= 64
                   && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                   && value is not ("." or "..");
        if (safe)
        {
            return value;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "h-" + string.Concat(hash.Take(16).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>The on-disk shape: a flat array of (root, path, base64) — readable enough that the
    /// owner can see which files are being carried forward and delete the file to reset them.</summary>
    private sealed record StoredEntry(string Root, string Path, string Content);
}
