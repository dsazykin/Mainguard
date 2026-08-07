using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The decisive leg of "stop making me re-approve every command": a grant made inside a real jail is
/// still there, <b>inside a different real jail</b>, after the first one is destroyed.
///
/// <para><b>Why a Docker test and not a fake.</b> Every hop is somewhere this has been silently broken
/// before. The jail's <c>$HOME</c> is a 256 MiB <b>tmpfs</b> mounted over the image's home, so a file
/// written there exists only in RAM and dies with the container. <c>/workspace</c> is a bind mount of a
/// per-agent worktree the daemon deletes at teardown. The restore goes back in over exec <b>stdin</b>
/// rather than <c>docker cp</c>, because <c>docker cp</c> writes into the image layer UNDERNEATH the
/// tmpfs and reports success — the file is then invisible to everything in the container while every
/// daemon-side signal says it landed. None of that is observable against a fake engine, so a fake would
/// assert the shape of the round trip while proving nothing about whether it happens. The assertion is
/// therefore made by reading the file <b>from inside the second container</b>, never host-side.</para>
///
/// <para>The pieces are the production ones: <see cref="SandboxAgentLauncher.HarvestCliSettingsAsync"/>,
/// the real <see cref="CliSettingsStore"/> (pointed at a temp root, so nothing touches the owner's
/// store), and <c>DockerSandboxEngine</c>'s restore, over jails built by the real
/// <c>ContainerSpecBuilder</c>. The trust gates that decide WHEN this runs are pinned separately by
/// <c>CliSettingsBoundaryTests</c>; this is the mechanism they gate.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class CliSettingsRoundTripDockerTests
{
    private const string AgentKind = "probe-cli";
    private const string RepoHandle = "settings-roundtrip-repo";

    /// <summary>Where the CLI records "yes, and don't ask again" — the project tree, not the home.
    /// Nested, so the restore's <c>mkdir -p</c> of the parent is exercised rather than assumed.</summary>
    private static readonly AdapterSettingsPath WorkspaceEntry =
        new("workspace", ".probe/settings.local.json");

    /// <summary>The user-level file, on the other root, so both trees are proven in one run.</summary>
    private static readonly AdapterSettingsPath HomeEntry = new("home", ".probe/settings.json");

    [RequiresDockerFact]
    public async Task AGrantMadeInOneJail_IsPresentInsideAFreshJail()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        // Per-run nonces: neither a leftover container nor a file that happened to already be there can
        // satisfy the final assertions.
        var grant = "{\"permissions\":{\"allow\":[\"Bash(probe-" + Guid.NewGuid().ToString("N")[..8] + ":*)\"]}}";
        var userPref = "{\"model\":\"probe-" + Guid.NewGuid().ToString("N")[..8] + "\"}";

        using var registry = new TempRegistry(AgentKind, WorkspaceEntry, HomeEntry);
        var launcher = new SandboxAgentLauncher(
            new EngineOnlyEnvironment(fixture.Engine), new InstalledAdapterCatalog(registry.Path));

        using var store = new TempSettingsStore();

        // ---- jail #1: the user approves a command in the terminal ---------------------------------
        // jailWritableWorktree: the jail writes into /workspace on both legs (the simulated approval
        // here, the restore's own `mkdir -p` below), and a default-mode temp worktree measures the
        // RUNNER's uid mapping rather than this feature — see NewJailWritableTempWorktree.
        var first = await fixture.SpawnAsync(
            agentId: "settings-roundtrip-1", ct: ct, jailWritableWorktree: true);
        await WriteInJailAsync(fixture, first.ContainerId, JailPathOf(WorkspaceEntry), grant);
        await WriteInJailAsync(fixture, first.ContainerId, JailPathOf(HomeEntry), userPref);

        // ---- harvest: jail → the per-repo host store ------------------------------------------------
        var harvested = await launcher.HarvestCliSettingsAsync(first.ContainerId, AgentKind, ct);
        Assert.Equal(2, harvested.Count);
        Assert.Equal(
            grant,
            Encoding.UTF8.GetString(harvested.Single(f => f.Root == AdapterSettingsRoot.Workspace).Content));

        // The client's own persistence step — the real store, so what is asserted below came off disk.
        Assert.True(store.Store.Save(RepoHandle, AgentKind, ToClient(harvested)));

        // ---- teardown: the tmpfs home and the worktree both go with the jail ------------------------
        await fixture.Engine.RemoveAsync(first.ContainerId, ct);

        // ---- jail #2: a fresh jail, seeded from the store -------------------------------------------
        var restored = store.Store.Load(RepoHandle, AgentKind);
        Assert.Equal(2, restored.Count);

        var second = await fixture.SpawnAsync(
            agentId: "settings-roundtrip-2", ct: ct,
            cliSettings: SandboxSettings(restored), jailWritableWorktree: true);

        // Read from INSIDE the container. Framed rather than substring-matched: a probe that failed to
        // run prints no frame at all, which would otherwise be indistinguishable from a mismatch.
        Assert.Equal(grant, await ReadInJailAsync(fixture, second.ContainerId, JailPathOf(WorkspaceEntry)));
        Assert.Equal(userPref, await ReadInJailAsync(fixture, second.ContainerId, JailPathOf(HomeEntry)));
    }

    [RequiresDockerFact]
    public async Task AJailGivenNoSettings_HasNoApprovedCommandsAtAll()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        // What an untrusted (external pull request) jail is spawned with: nothing. The point of reading
        // it back from inside the container is that "the daemon passed null" and "the container has no
        // allowlist" are different claims, and only the second one is the security property.
        var jail = await fixture.SpawnAsync(agentId: "settings-untrusted-1", ct: ct, cliSettings: null);

        Assert.Equal(string.Empty, await ReadInJailAsync(fixture, jail.ContainerId, JailPathOf(WorkspaceEntry)));
        Assert.Equal(string.Empty, await ReadInJailAsync(fixture, jail.ContainerId, JailPathOf(HomeEntry)));
    }

    [RequiresDockerFact]
    public async Task ALiveJailsOwnSettings_AreNeverClobberedByTheStoredCopy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        var inJail = "{\"permissions\":{\"allow\":[\"Bash(fresher-" + Guid.NewGuid().ToString("N")[..8] + ":*)\"]}}";

        // Both spawns name the SAME (repo, agent) and the SAME worktree, so the second one takes the
        // engine's REUSE path — start-if-stopped, then restore — against a jail that already holds the
        // user's latest approvals.
        await fixture.EnsureEgressReadyAsync(ct);
        var repoHash = "sbxreuse" + Guid.NewGuid().ToString("N")[..8];
        var worktree = fixture.NewJailWritableTempWorktree();

        var first = await fixture.Engine.SpawnAsync(Request(repoHash, worktree, fixture, null), ct);
        try
        {
            await WriteInJailAsync(fixture, first.ContainerId, JailPathOf(WorkspaceEntry), inJail);

            var second = await fixture.Engine.SpawnAsync(
                Request(repoHash, worktree, fixture, new[]
                {
                    new SandboxSettingsFile(
                        AdapterSettingsRoot.Workspace, WorkspaceEntry.Path,
                        Encoding.UTF8.GetBytes("{\"stale\":true}")),
                }),
                ct);

            // If this is not the same container the assertion below would be about a different jail.
            Assert.True(second.Reused, "the second spawn must have reused the first jail");
            Assert.Equal(first.ContainerId, second.ContainerId);

            // Write-if-absent: the host's older allowlist must never overwrite approvals the user has
            // just made in a live jail. The same rule the credential restore lives by, and the reason a
            // relaunch is safe rather than destructive.
            Assert.Equal(inJail, await ReadInJailAsync(fixture, first.ContainerId, JailPathOf(WorkspaceEntry)));
        }
        finally
        {
            try { await fixture.Engine.RemoveAsync(first.ContainerId, CancellationToken.None); }
            catch { /* never fail a test from cleanup */ }
        }
    }

    [RequiresDockerFact]
    public async Task ARestoredWorkspaceSettingsFile_IsNeverCommittedIntoTheUsersRepository()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        // /workspace IS the agent's git worktree, and the keep-alive rebase cycle's dirty-tree path is
        // `git add -A && git commit`. So a restored settings file that git can see would be committed
        // into the user's branch and merged to main — Mainguard silently writing to their history.
        await fixture.EnsureEgressReadyAsync(ct);
        var repoHash = "sbxignore" + Guid.NewGuid().ToString("N")[..8];
        var worktree = fixture.NewJailWritableTempWorktree();

        var first = await fixture.Engine.SpawnAsync(Request(repoHash, worktree, fixture, null), ct);
        try
        {
            await MakeWorkspaceAGitRepoAsync(fixture, first.ContainerId);

            // The restore runs on the reuse path against that now-real repository.
            var second = await fixture.Engine.SpawnAsync(
                Request(repoHash, worktree, fixture, new[]
                {
                    new SandboxSettingsFile(
                        AdapterSettingsRoot.Workspace, WorkspaceEntry.Path,
                        Encoding.UTF8.GetBytes("{\"permissions\":{\"allow\":[\"Bash(ls:*)\"]}}")),
                }),
                ct);
            Assert.True(second.Reused, "the second spawn must have reused the first jail");

            // The file really is there — otherwise a clean `git status` would prove nothing at all.
            Assert.NotEqual(
                string.Empty, await ReadInJailAsync(fixture, first.ContainerId, JailPathOf(WorkspaceEntry)));

            var status = await fixture.ExecAsync(first.ContainerId, "sh", "-c",
                "cd /workspace && printf 'BEGIN['; git status --porcelain; printf ']END'");
            Assert.Equal(0, status.ExitCode);
            Assert.Equal("BEGIN[]END", status.Stdout.Trim());
        }
        finally
        {
            try { await fixture.Engine.RemoveAsync(first.ContainerId, CancellationToken.None); }
            catch { /* never fail a test from cleanup */ }
        }
    }

    [RequiresDockerFact]
    public async Task TheFirstSessionsOwnSettingsFile_IsIgnoredEvenThoughNothingWasRestored()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        // The session that matters most: a repository with no stored approvals, so the restore payload
        // is EMPTY and the CLI creates the settings file itself when the user first approves something.
        // An ignore list derived from the restore payload would leave exactly this session unprotected.
        await fixture.EnsureEgressReadyAsync(ct);
        var repoHash = "sbxfirst" + Guid.NewGuid().ToString("N")[..8];
        var worktree = fixture.NewJailWritableTempWorktree();

        var first = await fixture.Engine.SpawnAsync(Request(repoHash, worktree, fixture, null), ct);
        try
        {
            var init = await fixture.ExecAsync(first.ContainerId, "sh", "-c",
                "cd /workspace && git init -q && git config user.email a@b.c && git config user.name t "
                + "&& git commit -q --allow-empty -m base && echo READY");
            Assert.True(init.ExitCode == 0 && init.Stdout.Contains("READY", StringComparison.Ordinal),
                $"could not make /workspace a git repo: exit={init.ExitCode} stderr={init.Stderr}");

            // Re-enter the engine with the DECLARED path but no restore payload — the first-session shape.
            var second = await fixture.Engine.SpawnAsync(
                Request(repoHash, worktree, fixture, null) with
                {
                    WorkspaceIgnorePaths = new[] { WorkspaceEntry.Path },
                },
                ct);
            Assert.True(second.Reused, "the second spawn must have reused the first jail");

            // Now the CLI writes its own approval, exactly as it does in-terminal.
            await WriteInJailAsync(fixture, first.ContainerId, JailPathOf(WorkspaceEntry),
                "{\"permissions\":{\"allow\":[\"Bash(ls:*)\"]}}");

            var status = await fixture.ExecAsync(first.ContainerId, "sh", "-c",
                "cd /workspace && printf 'BEGIN['; git status --porcelain; printf ']END'");
            Assert.Equal(0, status.ExitCode);
            Assert.Equal("BEGIN[]END", status.Stdout.Trim());
        }
        finally
        {
            try { await fixture.Engine.RemoveAsync(first.ContainerId, CancellationToken.None); }
            catch { /* never fail a test from cleanup */ }
        }
    }

    /// <summary>A spawn request for a FIXED (repo, agent, worktree), so two calls address one jail.</summary>
    private static SandboxSpawnRequest Request(
        string repoHash, string worktree, SandboxFixture fixture, IReadOnlyList<SandboxSettingsFile>? settings) =>
        new(
            RepoHash: repoHash,
            AgentId: "settings-writeifabsent-1",
            WorktreePath: worktree,
            ImageRef: fixture.ImageRef,
            Limits: new SandboxLimits(1L * 1024 * 1024 * 1024, 256),
            Secrets: new SandboxSecrets(
                new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test-not-a-real-key" },
                OobKey: new byte[32]),
            AgentUid: 1000,
            SupervisorUid: 1001,
            CliSettingsFiles: settings);

    /// <summary>The declared file's absolute path inside the jail — resolved through the SAME mapping
    /// the engine and the harvest use, so a drift in that mapping fails here rather than silently
    /// writing somewhere nothing reads.</summary>
    private static string JailPathOf(AdapterSettingsPath entry) =>
        DockerSandboxEngine.SettingsRootPath(entry.ParsedRoot) + "/" + entry.Path;

    /// <summary>
    /// Turns the jail's <c>/workspace</c> into a real repository, so <c>git status</c> has an opinion at
    /// all. (A jail whose workspace is not a repo is the substrate-less case, where the ignore step must
    /// simply do nothing.)
    ///
    /// <para><b>Two details are load-bearing and both were found by CI, not by reasoning.</b> The
    /// worktree is bind-mounted from the host and owned by the TEST PROCESS's uid, while git runs as the
    /// AGENT uid — so git's dubious-ownership protection rejects the repository it just created, and the
    /// symptom is the thoroughly misleading <c>fatal: not in a git directory</c> from the NEXT command
    /// rather than a failure from <c>git init</c> (which exits 0). Hence
    /// <c>safe.directory</c>. And the identity is passed with <c>-c</c> rather than written by
    /// <c>git config</c>, so the setup never depends on local config being writable.</para>
    ///
    /// <para>Reproduced outside this suite before being fixed: the same 0777 bind mount entered as a
    /// foreign uid gives <c>init=0</c> then <c>config=128 fatal: not in a git directory</c>, and adding
    /// <c>safe.directory</c> makes both succeed.</para>
    /// </summary>
    private static async Task MakeWorkspaceAGitRepoAsync(SandboxFixture fixture, string containerId)
    {
        var init = await fixture.ExecAsync(containerId, "sh", "-c",
            "cd /workspace || exit 90\n"
            + "git config --global --add safe.directory /workspace || exit 91\n"
            + "git init -q || exit 92\n"
            + "git -c user.email=a@b.c -c user.name=t commit -q --allow-empty -m base || exit 93\n"
            + "echo READY\n");
        Assert.True(init.ExitCode == 0 && init.Stdout.Contains("READY", StringComparison.Ordinal),
            $"could not make /workspace a git repo: exit={init.ExitCode} "
            + "(90=cd 91=safe.directory 92=init 93=commit) "
            + $"stdout={init.Stdout.Trim()} stderr={init.Stderr.Trim()}");
    }

    private static async Task WriteInJailAsync(
        SandboxFixture fixture, string containerId, string path, string content)
    {
        var wrote = await fixture.ExecAsync(containerId,
            "sh", "-c", $"mkdir -p \"$(dirname '{path}')\" && printf '%s' '{content}' > '{path}'");
        Assert.True(wrote.ExitCode == 0,
            $"could not simulate the in-jail approval at {path}: exit={wrote.ExitCode} stderr={wrote.Stderr}");
    }

    /// <summary>The file's contents as the CONTAINER sees them, or empty when it is not there. Framed so
    /// "the probe never ran" cannot masquerade as "the file was empty".</summary>
    private static async Task<string> ReadInJailAsync(SandboxFixture fixture, string containerId, string path)
    {
        var read = await fixture.ExecAsync(containerId,
            "sh", "-c", $"printf 'BEGIN['; cat '{path}' 2>/dev/null; printf ']END'");
        Assert.Equal(0, read.ExitCode);
        var output = read.Stdout.Trim();
        Assert.StartsWith("BEGIN[", output, StringComparison.Ordinal);
        Assert.EndsWith("]END", output, StringComparison.Ordinal);
        return output[6..^4];
    }

    private static IReadOnlyList<CliSettingsFileEntry> ToClient(IReadOnlyList<SandboxSettingsFile> files) =>
        files.Select(f => new CliSettingsFileEntry(
            AdapterSettingsPath.SpellRoot(f.Root), f.RelativePath, f.Content)).ToArray();

    private static IReadOnlyList<SandboxSettingsFile> SandboxSettings(IReadOnlyList<CliSettingsFileEntry> files) =>
        files.Select(f =>
        {
            Assert.True(AdapterSettingsPath.TryParseRoot(f.Root, out var root), $"unknown root '{f.Root}'");
            return new SandboxSettingsFile(root, f.Path, f.Content);
        }).ToArray();

    /// <summary>The real host store, pointed at a temp directory — the owner's own settings are never
    /// read, written or looked at by this suite.</summary>
    private sealed class TempSettingsStore : IDisposable
    {
        private readonly string _root;

        public TempSettingsStore()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "mg-settings-store-" + Guid.NewGuid().ToString("N")[..8]);
            Store = new CliSettingsStore(_root);
        }

        public CliSettingsStore Store { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* never fail a test from cleanup */ }
        }
    }

    /// <summary>A temp adapter registry holding ONE install marker — the daemon's only declaration of
    /// which files it may harvest from (and restore into) a jail.</summary>
    private sealed class TempRegistry : IDisposable
    {
        public TempRegistry(string agentKind, params AdapterSettingsPath[] settingsPaths)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mg-settings-registry-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
            File.WriteAllText(
                System.IO.Path.Combine(Path, agentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    agentKind, "1.0.0", new[] { "/bin/true" }, SettingsPaths: settingsPaths)));
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* never fail a test from cleanup */ }
        }
    }

    /// <summary>The substrate slice the harvest actually uses: the sandbox engine and nothing else. The
    /// jails come from <see cref="SandboxFixture"/> (built with the production
    /// <c>ContainerSpecBuilder</c>), so every other member throws rather than quietly answering.</summary>
    private sealed class EngineOnlyEnvironment : IAgentEnvironment
    {
        public EngineOnlyEnvironment(ISandboxEngine sandboxes) => Sandboxes = sandboxes;

        public string SubstrateId => "docker-test";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public ISandboxEngine Sandboxes { get; }

        public IRepoProvisioner Repos =>
            throw new NotSupportedException("the harvest path never provisions a repo");

        public IAgentWorktreeManager Worktrees =>
            throw new NotSupportedException("the harvest path never touches a worktree");

        public IEgressPolicy Egress =>
            throw new NotSupportedException("the harvest path never touches egress");

        public SyncRemote ResolveSyncRemote(string repoHash) =>
            throw new NotSupportedException("the harvest path never resolves a sync remote");
    }
}
