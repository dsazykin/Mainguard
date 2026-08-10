using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// An egress-allowlist edit must SURVIVE A RESTART.
///
/// <para><b>The defect.</b> <c>EgressAllowlist.ToPersistedForm</c>/<c>FromPersistedForm</c> existed with
/// no production callers on either side (compile-proven). <c>Wsl2AgentEnvironment</c> built
/// <c>EgressAllowlist.WithDefaults(audit)</c> on every daemon start, so
/// <c>EgressGrpcService.AddAllowlistHost</c>/<c>RemoveAllowlistHost</c> mutated an in-memory list: the
/// change was audited, re-rendered onto the running proxy, and then silently reverted by the next daemon
/// restart or WSL idle-stop. The user re-approved the same host forever, and the audit log recorded each
/// re-approval as though it were a fresh decision.</para>
///
/// <para><b>Both directions matter, and the removal is the security-relevant one.</b> An add that
/// reverts is an annoyance; a REMOVAL that reverts silently re-opens a host the user deliberately took
/// away, and the proxy is rendered from this list on the very next spawn. Both are asserted here.</para>
///
/// <para>The round-trip tests go through the real <see cref="Wsl2AgentEnvironment"/> — the production
/// wiring, and the line that was wrong — rather than through the store directly. Constructing it needs
/// no live Docker (its client connects lazily), and "a restart" is modelled the way a restart actually
/// works: a second environment built over the same VM root.</para>
/// </summary>
public class EgressAllowlistPersistenceTests : IDisposable
{
    private const string Who = "test-operator";

    /// <summary>A host that is NOT in <see cref="EgressAllowlist.DefaultEntries"/>, so its presence can
    /// only have come from the persisted edit.</summary>
    private const string AddedHost = "internal-registry.example.com";

    /// <summary>A host that IS a shipped default, so a reverted removal is visible as it coming back.</summary>
    private const string DefaultHost = "pypi.org";

    private readonly string _vmRoot = Path.Combine(
        Path.GetTempPath(), "mg-allowlist-persist-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A daemon start over this VM root — i.e. exactly what a restart re-runs.</summary>
    private EgressAllowlist StartDaemon() =>
        new Wsl2AgentEnvironment(vmRoot: _vmRoot, auditLog: new InMemoryAuditLog()).Egress.Allowlist;

    // ---- the round trip, through the production wiring ------------------------------------------

    [Fact]
    public void AHostTheUserAdded_IsStillAllowed_AfterTheDaemonRestarts()
    {
        var first = StartDaemon();
        Assert.False(first.Allows(AddedHost)); // precondition: not a default

        first.Add(new EgressAllowlistEntry("Internal registry", AddedHost, EgressEntryKind.PackageRegistry), Who);
        Assert.True(first.Allows(AddedHost));

        // The restart.
        var second = StartDaemon();

        Assert.True(second.Allows(AddedHost),
            "the allowlist edit did not survive the restart — the user must approve this host again");

        // It came back as itself, not as some lossy reconstruction: name and kind round-trip too,
        // because the kind drives both the UI grouping and the A6 git-host warning.
        var entry = Assert.Single(second.Entries, e => e.HostPattern == AddedHost);
        Assert.Equal("Internal registry", entry.Name);
        Assert.Equal(EgressEntryKind.PackageRegistry, entry.Kind);
    }

    /// <summary>
    /// The direction that is a security defect rather than an inconvenience: a removal that silently
    /// reverts puts the host back in the filter the proxy renders on the next spawn, so the user's
    /// decision to cut off a destination is undone without anybody being told.
    /// </summary>
    [Fact]
    public void AHostTheUserRemoved_StaysRemoved_AfterTheDaemonRestarts()
    {
        var first = StartDaemon();
        Assert.True(first.Allows(DefaultHost)); // precondition: a shipped default

        Assert.True(first.Remove(DefaultHost, Who));
        Assert.False(first.Allows(DefaultHost));

        var second = StartDaemon();

        Assert.False(second.Allows(DefaultHost),
            "a removed host came back after the restart — the proxy will permit it again on the next spawn");
    }

    /// <summary>
    /// Non-vacuity: with nothing saved, a daemon still starts on the shipped default-deny set. A "fix"
    /// that persisted an empty list on first run would produce a proxy that permits NOTHING, and one
    /// that failed to fall back would stop the daemon booting at all.
    /// </summary>
    [Fact]
    public void AFirstRun_GetsTheShippedDefaults_IncludingNoGitHost()
    {
        var allowlist = StartDaemon();

        Assert.Equal(
            EgressAllowlist.DefaultEntries.Select(e => e.HostPattern).OrderBy(h => h, StringComparer.Ordinal),
            allowlist.Entries.Select(e => e.HostPattern).OrderBy(h => h, StringComparer.Ordinal));

        // A6 survives the new load path: the git host is still absent by construction.
        Assert.False(allowlist.HasGitHostEntry);
    }

    // ---- the store's own failure behaviour -------------------------------------------------------

    /// <summary>
    /// A corrupt store falls back to the DEFAULTS rather than throwing. This is read during daemon
    /// construction, so the alternative is a daemon that will not start — the user would lose every
    /// agent to a damaged preferences file. The fallback is also the safe direction: the shipped
    /// restrictive set, never a wider one.
    /// </summary>
    [Fact]
    public void ACorruptStore_FallsBackToTheDefaults_RatherThanStoppingTheDaemon()
    {
        Directory.CreateDirectory(_vmRoot);
        File.WriteAllText(
            Path.Combine(_vmRoot, FileEgressAllowlistStore.FileName), "{ this is not the persisted form");

        var allowlist = StartDaemon();

        Assert.Equal(EgressAllowlist.DefaultEntries.Count, allowlist.Entries.Count);
        Assert.False(allowlist.Allows(AddedHost));
    }

    /// <summary>
    /// The auto-permitted hosts an installed CLI declares must NOT be written into the user's saved
    /// allowlist. <c>CombinedWith</c> is a render-time union used to build the proxy config; persisting
    /// it would bake those hosts in permanently, where they would outlive the CLI that justified them
    /// and appear to the user as entries they never added.
    /// </summary>
    [Fact]
    public void AutoPermittedCliHosts_AreNotWrittenIntoTheUsersSavedAllowlist()
    {
        var store = new FileEgressAllowlistStore(Path.Combine(_vmRoot, FileEgressAllowlistStore.FileName));
        var allowlist = EgressAllowlist.LoadOrDefaults(new InMemoryAuditLog(), store);

        var combined = allowlist.CombinedWith(
            new[] { "platform.claude.com" }, EgressEntryKind.AgentService, "Agent CLI");

        // The render-time view sees it…
        Assert.True(combined.Allows("platform.claude.com"));

        // …and an edit made through the view does not reach the user's file (it carries no store), so a
        // restart does not inherit it.
        combined.Add(new EgressAllowlistEntry("x", "auto.example.com", EgressEntryKind.Custom), Who);

        Assert.False(StartDaemon().Allows("auto.example.com"));
        Assert.False(StartDaemon().Allows("platform.claude.com"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_vmRoot, recursive: true); } catch { /* never fail a test from cleanup */ }
    }
}
