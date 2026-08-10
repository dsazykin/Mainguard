using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>What an allowlist entry is for — drives the UI grouping and the A6 git-host warning.</summary>
public enum EgressEntryKind
{
    ModelApi,
    PackageRegistry,
    GitHost,
    Custom,

    /// <summary>A host an installed agent CLI needs to function (e.g. claude-code's
    /// <c>platform.claude.com</c> auth/console) — auto-permitted from the CLI's declared
    /// <see cref="Adapters.AdapterSpec.EgressHosts"/>. Direct-route like a package registry (NOT
    /// gateway-fronted — it is not a model-completion host), never a git host.</summary>
    AgentService,
}

/// <summary>
/// One egress-allowlist entry: a friendly <paramref name="Name"/> and a <paramref name="HostPattern"/>
/// (a hostname the proxy + pinned DNS will answer). <see cref="DefeatsA6"/> flags an entry that
/// re-opens a direct route to a git host — the A6 structural control is the git host's <b>absence</b>
/// from the agent allowlist, so a user-added git-host entry is surfaced as defeating it.
/// </summary>
public sealed record EgressAllowlistEntry(string Name, string HostPattern, EgressEntryKind Kind)
{
    /// <summary>True iff this entry re-opens a direct git-host route (A6 defeated).</summary>
    public bool DefeatsA6 => Kind == EgressEntryKind.GitHost || LooksLikeGitHost(HostPattern);

    /// <summary>Recognises a hostname as a git host (known providers or a "git"-prefixed host).</summary>
    public static bool LooksLikeGitHost(string hostPattern)
    {
        if (string.IsNullOrWhiteSpace(hostPattern)) return false;
        var host = hostPattern.Trim().ToLowerInvariant();

        var (_, kind) = GitHostDetector.Detect("https://" + host + "/owner/repo.git");
        if (kind != Mainguard.Git.Models.HostKind.Unknown) return true;

        // Self-hosted / enterprise git hosts commonly carry a "git." label.
        return host.StartsWith("git.", StringComparison.Ordinal)
            || host.StartsWith("git-", StringComparison.Ordinal)
            || host is "github.com" or "gitlab.com" or "bitbucket.org"
            || host.EndsWith(".github.com", StringComparison.Ordinal)
            || host.EndsWith(".gitlab.com", StringComparison.Ordinal);
    }
}

/// <summary>
/// Where the user's allowlist edits are kept so they survive a restart. Injected rather than assumed
/// so tests (and a future per-repo scope) can substitute a store without touching a real path.
/// </summary>
public interface IEgressAllowlistStore
{
    /// <summary>The persisted <see cref="EgressAllowlist.ToPersistedForm"/> payload, or null when
    /// nothing has been saved yet (a first run) or it could not be read.</summary>
    string? Load();

    /// <summary>Persists the payload. Called after every user edit.</summary>
    void Save(string persistedForm);
}

/// <summary>
/// The default-deny egress allowlist (P2-07 §3.3): the model APIs and package registries an agent
/// may reach through the proxy. It is user-visible and editable; every add/remove emits an
/// <c>allowlist_changed</c> audit event (feeds P2-17 transparency / P2-15 chaining). The provisioned
/// repo's git host is <b>deliberately not a default</b> (A6) — git-sourced installs go through the
/// daemon read-only git proxy, never the agent's own egress.
///
/// <para><b>Edits are DURABLE (and were not).</b> <see cref="ToPersistedForm"/> and
/// <see cref="FromPersistedForm"/> existed with no production callers on either side —
/// <c>Wsl2AgentEnvironment</c> built <see cref="WithDefaults"/> on every daemon start, so
/// <c>EgressGrpcService.AddAllowlistHost</c>/<c>RemoveAllowlistHost</c> mutated an in-memory list that
/// was audited, re-rendered onto the live proxy, and then silently reverted by the next daemon restart
/// or WSL idle-stop. The user re-approved the same host forever, and the audit log dutifully recorded
/// each approval as if it were a new decision. A <see cref="IEgressAllowlistStore"/> makes the edit
/// outlive the process; <see cref="LoadOrDefaults"/> is the production entry point.</para>
/// </summary>
public sealed class EgressAllowlist
{
    private readonly List<EgressAllowlistEntry> _entries;
    private readonly IAuditLog _audit;
    private readonly IEgressAllowlistStore? _store;

    public const string ChangeEventType = "allowlist_changed";

    public EgressAllowlist(
        IEnumerable<EgressAllowlistEntry> entries, IAuditLog audit, IEgressAllowlistStore? store = null)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _entries = entries?.ToList() ?? throw new ArgumentNullException(nameof(entries));
        _store = store;
    }

    /// <summary>The current entries (snapshot).</summary>
    public IReadOnlyList<EgressAllowlistEntry> Entries => _entries.ToArray();

    /// <summary>True iff any entry re-opens a git-host route (A6 defeated) — surfaced by the UI.</summary>
    public bool HasGitHostEntry => _entries.Any(e => e.DefeatsA6);

    /// <summary>Does the allowlist permit <paramref name="host"/> (exact or suffix-wildcard match)?</summary>
    public bool Allows(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().ToLowerInvariant();
        return _entries.Any(e => HostMatches(e.HostPattern, h));
    }

    /// <summary>Adds an entry and emits the change event; a duplicate (by host) is a no-op.</summary>
    public void Add(EgressAllowlistEntry entry, string who)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entries.Any(e => string.Equals(e.HostPattern, entry.HostPattern, StringComparison.OrdinalIgnoreCase)))
            return;
        _entries.Add(entry);
        EmitChange("add", entry, who);
    }

    /// <summary>Removes the entry with <paramref name="hostPattern"/> and emits the change event.</summary>
    public bool Remove(string hostPattern, string who)
    {
        var existing = _entries.FirstOrDefault(e => string.Equals(e.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return false;
        _entries.Remove(existing);
        EmitChange("remove", existing, who);
        return true;
    }

    private void EmitChange(string action, EgressAllowlistEntry entry, string who)
    {
        _audit.Append(new AuditEvent(ChangeEventType, new Dictionary<string, string>
        {
            ["who"] = who,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["entry"] = entry.HostPattern,
            ["name"] = entry.Name,
            ["kind"] = entry.Kind.ToString(),
            ["action"] = action,
            ["defeats_a6"] = entry.DefeatsA6 ? "true" : "false",
        }));

        // Persist the EDIT, not just the record of it. Without this the audit event above is the only
        // trace the change ever happened: the entry itself dies with the process. Deliberately after the
        // audit append, so a store failure can never cost us the security record of the decision.
        _store?.Save(ToPersistedForm());
    }

    private static bool HostMatches(string pattern, string host)
    {
        var p = pattern.Trim().ToLowerInvariant();
        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = p[1..]; // ".example.com"
            return host.EndsWith(suffix, StringComparison.Ordinal) || host == p[2..];
        }
        return host == p;
    }

    /// <summary>
    /// The default entries: model APIs + package registries only. <b>No git host</b> (A6). This exact
    /// set is pinned by <c>EgressAllowlistTests.Defaults_ContainNoGitHostEntry</c>.
    /// </summary>
    public static IReadOnlyList<EgressAllowlistEntry> DefaultEntries { get; } = new[]
    {
        new EgressAllowlistEntry("Anthropic API", "api.anthropic.com", EgressEntryKind.ModelApi),
        new EgressAllowlistEntry("OpenAI API", "api.openai.com", EgressEntryKind.ModelApi),
        new EgressAllowlistEntry("npm registry", "registry.npmjs.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("PyPI", "pypi.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("PyPI files", "files.pythonhosted.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("NuGet API", "api.nuget.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("NuGet gallery", "www.nuget.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("crates.io", "crates.io", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("crates.io downloads", "static.crates.io", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("Go module proxy", "proxy.golang.org", EgressEntryKind.PackageRegistry),
    };

    /// <summary>An allowlist seeded with <see cref="DefaultEntries"/> and NO persistence — the shape
    /// tests and render-time views want. Production goes through <see cref="LoadOrDefaults"/>.</summary>
    public static EgressAllowlist WithDefaults(IAuditLog audit) => new(DefaultEntries, audit);

    /// <summary>
    /// The production constructor: the user's SAVED allowlist when there is one, the defaults on a first
    /// run — and either way bound to <paramref name="store"/>, so subsequent edits are written back.
    ///
    /// <para>A store that cannot be read, or holds something unparseable, falls back to the defaults
    /// rather than throwing. This runs during daemon construction, and the alternative to a default-deny
    /// allowlist built from a known-good set is a daemon that will not start at all — the user would
    /// lose every agent to a corrupted preferences file. The fallback is the SAFE direction: it is the
    /// same restrictive set the product ships with, never a wider one.</para>
    /// </summary>
    public static EgressAllowlist LoadOrDefaults(IAuditLog audit, IEgressAllowlistStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            if (store.Load() is { Length: > 0 } persisted)
            {
                return FromPersistedForm(persisted, audit, store);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            // Unreadable/corrupt: fall through to the defaults below.
        }

        return new EgressAllowlist(DefaultEntries, audit, store);
    }

    /// <summary>
    /// A render-time view of this allowlist unioned with <paramref name="extraHosts"/> as
    /// <paramref name="kind"/> entries — for building the proxy config that must also permit the
    /// installed agent CLIs' declared hosts (auto-permit on install). Deduped by host (case-insensitive);
    /// a host already present is kept as-is. This is NOT a user edit — construction emits no
    /// <c>allowlist_changed</c> audit event, so the persisted/editable allowlist is untouched.
    /// </summary>
    public EgressAllowlist CombinedWith(IEnumerable<string>? extraHosts, EgressEntryKind kind, string namePrefix)
    {
        if (extraHosts is null)
        {
            return this;
        }

        var present = _entries.Select(e => e.HostPattern.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var additions = new List<EgressAllowlistEntry>();
        foreach (var raw in extraHosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var host = raw.Trim();
            if (present.Add(host.ToLowerInvariant()))
            {
                additions.Add(new EgressAllowlistEntry($"{namePrefix} {host}", host, kind));
            }
        }

        // NO store on the combined view. This is a render-time union with the installed CLIs' declared
        // hosts, not a user edit — persisting it would write auto-permitted hosts into the user's saved
        // allowlist, where they would then outlive the CLI that justified them.
        return additions.Count == 0 ? this : new EgressAllowlist(_entries.Concat(additions), _audit);
    }

    /// <summary>Serialises the current entries.</summary>
    public string ToPersistedForm() =>
        JsonSerializer.Serialize(_entries.Select(e => new PersistedEntry(e.Name, e.HostPattern, e.Kind.ToString())).ToList());

    /// <summary>Rehydrates an allowlist from <see cref="ToPersistedForm"/> output (round-trips).
    /// <paramref name="store"/> binds the result for future edits; null yields a detached copy.</summary>
    public static EgressAllowlist FromPersistedForm(
        string json, IAuditLog audit, IEgressAllowlistStore? store = null)
    {
        var persisted = JsonSerializer.Deserialize<List<PersistedEntry>>(json) ?? new List<PersistedEntry>();
        var entries = persisted.Select(p =>
            new EgressAllowlistEntry(p.Name, p.HostPattern, Enum.Parse<EgressEntryKind>(p.Kind)));
        return new EgressAllowlist(entries, audit, store);
    }

    private sealed record PersistedEntry(string Name, string HostPattern, string Kind);
}

/// <summary>
/// The daemon-side allowlist file — one JSON document beside the mirrors and worktrees under the VM
/// root, so it shares their lifetime and their backup story.
///
/// <para>Writes are atomic (temp file + replace): the daemon re-reads this at every start, and a
/// half-written document read at boot would silently reset the user's allowlist to the defaults —
/// exactly the failure this store exists to end, arrived at by a different route. Read and write
/// failures are swallowed rather than thrown for the reason given on
/// <see cref="EgressAllowlist.LoadOrDefaults"/>: an unwritable preferences file must not take the
/// daemon down, and the in-memory edit still reaches the live proxy.</para>
/// </summary>
public sealed class FileEgressAllowlistStore : IEgressAllowlistStore
{
    /// <summary>The file name under the VM root.</summary>
    public const string FileName = "egress-allowlist.json";

    private readonly string _path;

    public FileEgressAllowlistStore(string path) =>
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A path is required.", nameof(path))
            : path;

    /// <summary>The store under a VM root (<c>&lt;vmRoot&gt;/egress-allowlist.json</c>).</summary>
    public static FileEgressAllowlistStore UnderVmRoot(string vmRoot) =>
        new(System.IO.Path.Combine(vmRoot, FileName));

    public string? Load()
    {
        try
        {
            return System.IO.File.Exists(_path) ? System.IO.File.ReadAllText(_path) : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string persistedForm)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var temp = _path + ".tmp";
            System.IO.File.WriteAllText(temp, persistedForm);
            System.IO.File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The edit still took effect in memory and on the live proxy; only its durability is lost.
        }
    }
}
