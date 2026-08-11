using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Gateway;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// MG-4, at the seam the existing tests left uncovered: the confinement is actually <b>MINTED</b> on a
/// spawn, and actually <b>REVOKED</b> on a stop.
///
/// <para><b>Why this file exists (the gap, measured).</b>
/// <see cref="BuildSecretsConfinementTests"/> calls <see cref="SandboxAgentLauncher.BuildSecrets"/>
/// directly and asserts that <i>given</i> a confinement the jail receives a token. It never asserts that
/// the launcher MINTS one — and its own <c>WithoutGateway_RealKeyStillGoesIn_UnchangedBehaviour</c> case
/// documents the null path as correct behaviour. So the machinery was tested and the invocation was not:
/// making <c>SandboxAgentLauncher.TryConfineToGatewayAsync</c> return null unconditionally left the whole
/// non-Docker server suite byte-identical to baseline. That is the exact regression #298 closed, whose
/// comment reads "the confinement machinery below was complete but never invoked" — and with the gateway
/// now ON by default, it means every BYOK jail gets the raw provider key, with no metering, no budget
/// and no custody.</para>
///
/// <para><b>These tests assert the CALL, not the callee.</b> Nothing here invokes <c>BuildSecrets</c>,
/// <c>TryConfineToGatewayAsync</c> or <c>AgentGatewayCredentials.Issue/Revoke</c>. They drive the shipped
/// <see cref="AgentSpawnService"/> out of the daemon's own container and then ask the daemon's own
/// <see cref="AgentGatewayCredentials"/> what it holds — the one observation that cannot be satisfied by
/// a spawn path that never called it. The substrate is fake (no Docker); the spawn chain, the launcher,
/// the stop path and the credential store are the production ones.</para>
/// </summary>
public sealed class GatewayConfinementWiringTests
{
    private const string AgentKind = "probe-cli";
    private const string RepoHandle = "confinement-wiring-repo";

    /// <summary>The BYOK key the user supplied. Custody of it is the whole subject.</summary>
    private const string RealKey = "sk-ant-REAL-PROVIDER-KEY-DO-NOT-LEAK";

    private const string ApiKeyVar = "PROBE_API_KEY";
    private const string BaseUrlVar = "PROBE_BASE_URL";
    private const string UpstreamHost = "api.anthropic.com";
    private const string GatewayBaseUrl = "http://10.0.0.7:5251";

    // =============================================================================================
    // 1. MG-4 — the confinement is minted on the spawn path.
    // =============================================================================================

    /// <summary>
    /// The test the suite was missing. A BYOK spawn that satisfies every confinement precondition must
    /// leave a credential in the daemon's custody: a token issued for this agent, the provider key held
    /// daemon-side, and the upstream binding recorded.
    ///
    /// <para>Asserted against <see cref="AgentGatewayCredentials"/> — the state the launcher can only
    /// have produced by CALLING it. A launcher whose confinement step returns null unconditionally
    /// passes every existing non-Docker test and fails this one.</para>
    /// </summary>
    [Fact]
    public async Task ABuyokSpawn_MintsAGatewayConfinement_RatherThanLeavingTheMachineryUninvoked()
    {
        using var rig = ConfinementRig.Create();

        var agentId = await rig.SpawnAsync(RealKey);

        // The token was ISSUED for this agent — the fact that only a real call to Issue() can produce.
        var token = rig.Credentials.TokenFor(agentId);
        Assert.False(string.IsNullOrEmpty(token), "the spawn path never minted a gateway confinement");
        Assert.StartsWith(AgentGatewayCredentials.TokenPrefix, token, StringComparison.Ordinal);

        // Custody: the real key is daemon-side, and the agent's upstream is bound (MG-20 — this is what
        // the gateway authenticates and routes on, instead of a spoofable client header).
        Assert.Equal(RealKey, rig.Credentials.ProviderKeyFor(agentId));
        Assert.Equal(UpstreamHost, rig.Credentials.UpstreamHostFor(agentId));

        // And the consequence the whole mechanism exists for: the jail got the TOKEN, not the key.
        var env = rig.Engine.LastSpawn!.Secrets.AgentEnv;
        Assert.Equal(token, env[ApiKeyVar]);
        Assert.Equal(GatewayBaseUrl, env[BaseUrlVar]);
        Assert.DoesNotContain(RealKey, string.Join("|", env.Values), StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control, and it is what keeps the test above honest: an interactive-login (OAuth)
    /// agent supplies no key, so there is nothing to confine and nothing must be minted. Without this a
    /// daemon that issued a credential to every agent unconditionally would satisfy the assertion above
    /// while breaking the OAuth path — the regression the MG-4 design is most careful about.
    /// </summary>
    [Fact]
    public async Task AnOAuthSpawn_MintsNothing_BecauseThereIsNoKeyToConfine()
    {
        using var rig = ConfinementRig.Create();

        var agentId = await rig.SpawnAsync(modelApiKey: null);

        Assert.Null(rig.Credentials.TokenFor(agentId));
        Assert.Null(rig.Credentials.UpstreamHostFor(agentId));
        Assert.DoesNotContain(BaseUrlVar, rig.Engine.LastSpawn!.Secrets.AgentEnv.Keys);
    }

    // =============================================================================================
    // 2. The stop path releases it.
    // =============================================================================================

    /// <summary>
    /// <c>AgentGatewayCredentials.Revoke</c> had <b>no production callers</b> — deleting it left both
    /// edition heads compiling. <c>Issue</c> ran on every BYOK spawn; <c>StopAsync</c> released the
    /// binder, the leader, the IPC endpoint and the terminal lock and called <c>TeardownAsync</c>, none
    /// of which touch credentials. So a stopped agent left a LIVE token — replayable by anything that
    /// had read it out of the jail — and a resident copy of the user's provider key, for the rest of the
    /// daemon's lifetime. Its own docstring already described the behaviour this test now pins.
    /// </summary>
    [Fact]
    public async Task StoppingAConfinedAgent_RevokesItsToken_AndDropsCustodyOfTheProviderKey()
    {
        using var rig = ConfinementRig.Create();

        var agentId = await rig.SpawnAsync(RealKey);
        var token = rig.Credentials.TokenFor(agentId);
        Assert.False(string.IsNullOrEmpty(token)); // precondition: there is something to revoke

        var stop = await rig.Spawns.StopAsync(agentId, CancellationToken.None);
        Assert.True(stop.Stopped);

        Assert.Null(rig.Credentials.TokenFor(agentId));
        Assert.Null(rig.Credentials.ProviderKeyFor(agentId));
        Assert.Null(rig.Credentials.UpstreamHostFor(agentId));

        // The stronger half: the token itself no longer authenticates. A revoke that dropped the
        // forward map but orphaned the reverse one would leave the credential usable at the gateway
        // while every lookup above answered null.
        Assert.Null(rig.Credentials.ResolveAgent(token));
    }

    // =============================================================================================
    // 3. Ticket #52 — a jail that receives the RAW KEY must say so.
    // =============================================================================================
    //
    // The gap was not that unconfinable CLIs exist — codex/qwen-code/opencode genuinely expose no
    // base-URL environment variable, re-verified against upstream source (see adapters.starter.json).
    // The gap was that the refusal was SILENT: TryConfineToGatewayAsync returned null without a word,
    // so a jail holding the user's real provider key and a jail holding a scoped session token
    // produced identical daemon logs. Nobody could answer "is my key inside that container?".
    //
    // These assert the WARNING, because an unasserted log line is deletable without anything failing,
    // and that is this repo's most reliably recurring defect.

    /// <summary>
    /// The shipped codex/opencode shape: the gateway is up and willing, the user supplied a BYOK key,
    /// and the CLI simply cannot be redirected. The key goes in — that is the deliberate, documented
    /// choice (breaking the CLI would be worse) — but it must be ANNOUNCED, naming the adapter and the
    /// variable the key landed under, so the log is enough to audit key custody.
    /// </summary>
    [Fact]
    public async Task AnUnconfinableCli_StillGetsTheRawKey_ButTheDaemonSaysSoOutLoud()
    {
        using var rig = ConfinementRig.Create(confinable: false);

        var agentId = await rig.SpawnAsync(RealKey);

        // Precondition: this really is the raw-key path, not a confinement that quietly worked.
        Assert.Null(rig.Credentials.TokenFor(agentId));
        var env = rig.Engine.LastSpawn!.Secrets.AgentEnv;
        Assert.Equal(RealKey, env[ApiKeyVar]);
        Assert.DoesNotContain(BaseUrlVar, env.Keys);

        var warning = Assert.Single(rig.Logs, l => l.Contains("confinement IMPOSSIBLE", StringComparison.Ordinal));
        Assert.Contains(agentId, warning, StringComparison.Ordinal);
        Assert.Contains(AgentKind, warning, StringComparison.Ordinal);   // WHICH agent is exposed
        Assert.Contains(ApiKeyVar, warning, StringComparison.Ordinal);   // and under which variable

        // The warning must never quote the key it is warning about — a log that leaks the secret is a
        // worse outcome than the silence it replaces.
        Assert.DoesNotContain(RealKey, warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other way a BYOK key reaches a jail unconfined: the gateway is switched off. Same exposure,
    /// different remedy (an operator setting rather than a vendor limitation), so it is a distinct
    /// message — an operator who reads "IMPOSSIBLE" would go looking for a CLI fix that does not apply.
    /// </summary>
    [Fact]
    public async Task WithTheGatewayOff_TheRawKeyStillGoesIn_AndIsReportedAsAConfigurationChoice()
    {
        using var rig = ConfinementRig.Create(gatewayEnabled: false);

        var agentId = await rig.SpawnAsync(RealKey);

        Assert.Equal(RealKey, rig.Engine.LastSpawn!.Secrets.AgentEnv[ApiKeyVar]);

        var warning = Assert.Single(rig.Logs, l => l.Contains("confinement OFF", StringComparison.Ordinal));
        Assert.Contains(agentId, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(RealKey, warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control, and the reason the key check comes FIRST in the launcher. An OAuth agent
    /// supplies no key, so nothing is exposed and there is nothing to warn about. Warning here would be
    /// worse than useless: it would fire on the most common spawn shape and train the operator to
    /// ignore exactly the messages above, which is how a loud signal becomes a silent one again.
    /// </summary>
    [Fact]
    public async Task AnOAuthSpawn_WarnsAboutNothing_BecauseNoKeyIsExposed()
    {
        using var rig = ConfinementRig.Create(confinable: false);

        await rig.SpawnAsync(modelApiKey: null);

        Assert.DoesNotContain(rig.Logs, l => l.Contains("confinement IMPOSSIBLE", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Logs, l => l.Contains("confinement OFF", StringComparison.Ordinal));
    }

    // =============================================================================================
    // The rig: an in-proc daemon, a fake substrate, the production spawn/stop chain.
    // =============================================================================================

    /// <summary>
    /// The daemon with its gateway ENABLED over a Docker-free substrate. The confinement preconditions
    /// the launcher checks are all satisfied here on purpose — a gateway base URL, an adapter declaring
    /// both a base-URL variable and a model host, and a proxy that can reach the gateway (the
    /// <see cref="IEgressPolicy"/> default for substrate-less doubles) — so the ONLY thing left that can
    /// make a confinement absent is the launcher failing to ask for one.
    /// </summary>
    private sealed class ConfinementRig : IDisposable
    {
        private readonly string _root;

        private ConfinementRig(string root) => _root = root;

        public required WebApplicationFactory<Program> Host { get; init; }

        public required RecordingSandboxEngine Engine { get; init; }

        /// <summary>
        /// The fixture the host was derived from — held because it owns the log-capture sink.
        /// <c>WithWebHostBuilder</c> returns a NEW factory but re-runs this fixture's
        /// <c>ConfigureWebHost</c>, so the provider registered there is this instance's, and
        /// <see cref="DaemonFixture.CapturedLogs"/> sees what the derived host logged.
        /// </summary>
        public required DaemonFixture Fixture { get; init; }

        /// <summary>What the daemon logged during this rig's lifetime.</summary>
        public IReadOnlyList<string> Logs => Fixture.CapturedLogs;

        public AgentSpawnService Spawns => Host.Services.GetRequiredService<AgentSpawnService>();

        public AgentGatewayCredentials Credentials =>
            Host.Services.GetRequiredService<AgentGatewayCredentials>();

        /// <param name="confinable">
        /// Whether the installed adapter declares the MG-4 pair. <c>false</c> models the shipped
        /// codex/qwen-code/opencode case — a CLI the vendor gives no base-URL variable — which is the
        /// branch ticket #52 is about.
        /// </param>
        /// <param name="gatewayEnabled">Whether the daemon's model gateway is configured at all.</param>
        public static ConfinementRig Create(bool confinable = true, bool gatewayEnabled = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "mg-gw-confine-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "repos", RepoHandle)); // "provisioned"

            // The install marker shape the VM installer writes. It declares the MG-4 confinement PAIR —
            // a base-URL variable (the CLI can be redirected) and a model host (we know where to
            // forward) — because a CLI missing either must NOT be confined.
            var registry = Path.Combine(root, "registry");
            Directory.CreateDirectory(registry);
            File.WriteAllText(
                Path.Combine(registry, AgentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    AgentKind, "1.0.0", new[] { "/bin/true" },
                    ApiKeyEnvVar: ApiKeyVar,
                    EgressHosts: null,
                    CredentialPaths: null,
                    BaseUrlEnvVar: confinable ? BaseUrlVar : null,
                    ModelHost: confinable ? UpstreamHost : null)));

            var engine = new RecordingSandboxEngine();
            var fixture = new DaemonFixture();
            var host = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(new FakeEnvironment(root, engine));
                services.AddSingleton(new InstalledAdapterCatalog(registry));

                // The gateway ON. In production this comes from the daemon's resolved bind address; the
                // address itself is irrelevant here because no request is issued — what matters is that
                // the spawn path is in the posture where it is SUPPOSED to confine.
                services.AddSingleton(gatewayEnabled
                    ? new GatewayConfinementOptions(GatewayBaseUrl, Enabled: true)
                    : GatewayConfinementOptions.Disabled);

                // The CLI bind would otherwise try a real `docker exec` PTY against a fake container id.
                services.AddSingleton(sp => new AgentCliBinder(
                    sp.GetRequiredService<TerminalSessionManager>(),
                    sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.SessionLeader>(),
                    sp.GetRequiredService<AgentSessionStore>(),
                    sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(),
                    _ => new InertTerminalSession()));
            }));

            return new ConfinementRig(root) { Host = host, Engine = engine, Fixture = fixture };
        }

        /// <summary>One spawn through the SHIPPED chain, returning the agent id it minted.</summary>
        public Task<string> SpawnAsync(string? modelApiKey) => Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey, role: string.Empty, CancellationToken.None);

        public void Dispose()
        {
            Host.Dispose();
            Fixture.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* never fail a test from cleanup */ }
        }

        /// <summary>Records what each spawn was given — so what actually reaches a jail is observable —
        /// and is otherwise inert.</summary>
        public sealed class RecordingSandboxEngine : ISandboxEngine
        {
            private readonly ConcurrentQueue<SandboxSpawnRequest> _spawns = new();

            public SandboxSpawnRequest? LastSpawn => _spawns.LastOrDefault();

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            {
                _spawns.Enqueue(request);
                return Task.FromResult(new SandboxHandle($"ctr-{request.AgentId}", Reused: false));
            }

            public Task<SandboxExecResult> ExecAsync(
                string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
                Task.FromResult(new SandboxExecResult(1, string.Empty, string.Empty));

            public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class FakeEnvironment : IAgentEnvironment
        {
            public FakeEnvironment(string root, ISandboxEngine sandboxes)
            {
                Sandboxes = sandboxes;
                Repos = new FakeProvisioner(root);
                Worktrees = new FakeWorktrees(root);
            }

            public string SubstrateId => "fake";

            public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

            public ISandboxEngine Sandboxes { get; }

            public IRepoProvisioner Repos { get; }

            public IAgentWorktreeManager Worktrees { get; }

            public IEgressPolicy Egress { get; } = new FakeEgress();

            public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

            private sealed class FakeProvisioner : IRepoProvisioner
            {
                private readonly string _root;

                public FakeProvisioner(string root) => _root = root;

                public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                    throw new NotSupportedException("not exercised");

                public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
            }

            private sealed class FakeWorktrees : IAgentWorktreeManager
            {
                private readonly string _root;

                public FakeWorktrees(string root) => _root = root;

                public string CreateAgentWorktree(string repoHash, string agentId)
                {
                    var path = Path.Combine(_root, "wt", repoHash, agentId);
                    Directory.CreateDirectory(path);
                    return path;
                }

                public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
                {
                    try { Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true); }
                    catch (DirectoryNotFoundException) { }
                }

                public void Prune(string repoHash) { }

                public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                    Array.Empty<Mainguard.Git.Models.WorktreeItem>();
            }

            private sealed class FakeEgress : IEgressPolicy
            {
                public EgressAllowlist Allowlist { get; } =
                    EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

                public string NetworkName => "fake-net";

                public string ProxyUrl => "http://fake-proxy:3128";

                public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

                public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
            }
        }

        private sealed class InertTerminalSession : ITerminalSession
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Stream IO { get; } = new MemoryStream();

            public Task<int> ExitCode => _exit.Task;

            public void Resize(int cols, int rows) { }

            public void Kill() => _exit.TrySetResult(0);

            public void Dispose() => Kill();
        }
    }
}
