using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Gateway;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-4 end to end, against real containers: a BYOK jail's model traffic transits the daemon's gateway,
/// the ledger is charged for it, an over-budget agent is refused, the provider key is nowhere inside the
/// container — and an OAuth agent is untouched.
///
/// <para><b>Why an in-process test could not have covered this.</b> PR #298 fixed the middleware's
/// attribution after discovering that every prior gateway test sent <c>Host: api.anthropic.com</c>, a
/// shape a confined jail cannot produce. This suite is the answer to the same question one layer out:
/// the request is issued <i>from inside a real hardened jail</i>, through the container's own
/// <c>HTTP_PROXY</c>, carrying the token and base URL that the real spawn path wrote into
/// <c>/run/secrets/agent.env</c>. Nothing about the request is constructed by the test.</para>
///
/// <para>That immediately found the defect this change fixes. The jail's proxy env points at tinyproxy
/// and <c>NO_PROXY</c> covers only loopback plus the internal git proxy, so a confined request reaches
/// tinyproxy naming the GATEWAY as its destination — and tinyproxy runs <c>FilterDefaultDeny</c> against
/// an allowlist that had never contained the daemon's own address. Confinement was not merely inert; had
/// the gateway been switched on, it would have BROKEN every BYOK agent it touched, with the refusal
/// coming from Mainguard's own proxy and looking exactly like a provider outage.</para>
///
/// <para><b>The one thing not real is the provider itself.</b> <see cref="ModelProxyMiddleware"/> always
/// dials <c>https://&lt;bound upstream&gt;</c>, i.e. api.anthropic.com; a test cannot bill a real key.
/// The daemon's upstream transport is therefore replaced (the <c>configureServices</c> seam) with a
/// recorder that answers from a <see cref="FakeModelEndpoint"/> — which also lets the test assert what
/// the daemon sent upstream. Everything from the jail up to that boundary is production code.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class GatewayConfinementDockerTests
{
    /// <summary>The BYOK key the user supplied. It must never appear inside a container.</summary>
    private const string RealKey = "sk-ant-REAL-PROVIDER-KEY-DO-NOT-LEAK";

    private const string AdapterId = "claude-code";
    private const string BaseUrlVar = "ANTHROPIC_BASE_URL";
    private const string ApiKeyVar = "ANTHROPIC_API_KEY";
    private const string UpstreamHost = "api.anthropic.com";

    /// <summary>41 tokens — the usage the fake provider reports, so the ledger assertion is exact.</summary>
    private const string ProviderBody =
        "{\"model\":\"claude-test\",\"usage\":{\"input_tokens\":25,\"output_tokens\":16},\"content\":\"pong\"}";

    // ---------------------------------------------------------------------------------------------
    // 1. The whole confined path, from inside the jail.
    // ---------------------------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task ConfinedByokJail_ReachesTheGatewayThroughItsOwnProxy_AndTheLedgerIsCharged()
    {
        await using var world = await ConfinementWorld.StartAsync(new BudgetCaps(0, 0, 0, 0));
        var jail = await world.LaunchAsync("byok-agent-1", modelApiKey: RealKey);

        // --- the jail was actually confined, and holds no provider key -----------------------------
        var env = await world.ReadSecretsFileAsync(jail);

        Assert.Contains(BaseUrlVar + "=" + world.GatewayBaseUrl, env, StringComparison.Ordinal);
        var token = ValueOf(env, ApiKeyVar);
        Assert.StartsWith("mg_sess_", token, StringComparison.Ordinal);

        // --- the request, issued the way a confined CLI issues it ----------------------------------
        // Sourced from the same env file the CLI sources (SandboxCliLaunch), sent through the
        // container's own proxy env. The test supplies no address, no header and no route.
        var call = await world.CurlFromJailAsync(
            jail,
            "curl -sS -o /tmp/out -w '%{http_code}' -X POST \"$" + BaseUrlVar + "/v1/messages\" "
            + "-H \"x-api-key: $" + ApiKeyVar + "\" -H 'content-type: application/json' "
            + "-d '{\"model\":\"claude-test\",\"max_tokens\":16}'; echo; head -c 300 /tmp/out");

        Assert.StartsWith("200", call, StringComparison.Ordinal);
        Assert.Contains("pong", call, StringComparison.Ordinal);

        // --- it really transited the gateway: the daemon substituted the key on the upstream leg ---
        var sent = Assert.Single(world.Upstream.Requests);
        Assert.Equal(UpstreamHost, sent.Host);
        Assert.Equal("https", sent.Scheme);
        Assert.Equal(RealKey, sent.ApiKeyHeader);          // the daemon's key went out…
        Assert.DoesNotContain(token, sent.AllHeaderValues, StringComparer.Ordinal);  // …the jail's did not

        // --- and the ledger was charged for it -----------------------------------------------------
        var ledger = world.Daemon.Services.GetRequiredService<BudgetLedger>();
        Assert.Equal(41, ledger.GetTotals(jail.AgentId).Tokens);
    }

    /// <summary>
    /// The provider key must be absent from every place inside the container a process can read it —
    /// the container spec's environment, the agent-readable credential tmpfs, and the agent process's
    /// own environment after the launch wrapper has sourced the env file.
    ///
    /// <para>Asserted against a live <c>docker inspect</c> and live <c>exec</c>s, not against
    /// <c>BuildSecrets</c>'s return value: a spec-builder assertion cannot see anything the runtime adds,
    /// and "the key is not in the object we built" is not the claim MG-4 makes.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ConfinedByokJail_ContainsNoProviderKey_AnywhereAProcessCanReadIt()
    {
        await using var world = await ConfinementWorld.StartAsync(new BudgetCaps(0, 0, 0, 0));
        var jail = await world.LaunchAsync("byok-agent-2", modelApiKey: RealKey);

        var inspect = await world.Docker.Containers.InspectContainerAsync(jail.ContainerId);
        var spec = string.Join("\n", inspect.Config.Env ?? new List<string>());
        Assert.DoesNotContain(RealKey, spec, StringComparison.Ordinal);

        // The jail mounts THIS catalog's adapters root, read-only. Pinned because the launcher used to
        // mount the fixed /home/mainguard/mainguard/adapters regardless of where its catalog pointed:
        // on a box that happens to have that directory the mismatch is invisible, and on one that does
        // not the container create fails outright. CI found it; this assertion is what makes it visible
        // here.
        var adapters = Assert.Single(
            inspect.Mounts, m => m.Destination == Mainguard.Agents.Agents.Adapters.AdapterPaths.SandboxMount);
        Assert.Equal(world.AdaptersRoot, adapters.Source);
        Assert.False(adapters.RW, "the adapters root must be mounted read-only");

        var secrets = await world.ReadSecretsFileAsync(jail);
        Assert.DoesNotContain(RealKey, secrets, StringComparison.Ordinal);

        // The environment the CLI itself runs with, after the wrapper sources the env file — the last
        // place the key could survive.
        var effective = await world.ExecAsync(
            jail, "set -a; . /run/secrets/agent.env; set +a; env");
        Assert.DoesNotContain(RealKey, effective, StringComparison.Ordinal);
        Assert.Contains("mg_sess_", effective, StringComparison.Ordinal);

        // A grep of the whole writable credential mount, in case a future change writes the key to a
        // sibling file rather than the env file.
        var swept = await world.ExecAsync(jail, "grep -rl 'sk-ant-REAL' /run/secrets 2>/dev/null; echo SWEPT");
        Assert.Equal("SWEPT", swept.Trim());
    }

    // ---------------------------------------------------------------------------------------------
    // 2. The budget is enforced on that same real path.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An over-budget agent gets a soft 402 and nothing reaches the provider. The cap is one token, so
    /// the first 41-token call lands the agent over budget and the second is refused — the refusal is
    /// therefore produced by the ledger this run actually wrote, not by a pre-seeded number.
    /// </summary>
    [RequiresDockerFact]
    public async Task OverBudgetConfinedJail_IsRefusedWithASoft402_AndNothingReachesTheProvider()
    {
        await using var world = await ConfinementWorld.StartAsync(new BudgetCaps(PerAgentTokenCap: 1, 0, 0, 0));
        var jail = await world.LaunchAsync("byok-agent-3", modelApiKey: RealKey);

        var first = await world.PostFromJailAsync(jail);
        Assert.StartsWith("200", first, StringComparison.Ordinal);
        Assert.Single(world.Upstream.Requests);

        var second = await world.PostFromJailAsync(jail);

        Assert.StartsWith("402", second, StringComparison.Ordinal);
        // The refusal happened before the network hop: still exactly one upstream request.
        Assert.Single(world.Upstream.Requests);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. The regression that matters most: an OAuth agent still works.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The highest-value test here.</b> An agent that authenticates interactively holds no API key, so
    /// it cannot be gateway-confined and must keep the exact behaviour it has today: no base-URL
    /// override, no gateway token, its login files restored from the keychain, and its provider host
    /// still carried by the egress proxy.
    ///
    /// <para>The proxy assertion is a paired verdict rather than a reachability check, so it is honest on
    /// an offline runner: <c>api.anthropic.com</c> must NOT be refused by tinyproxy's filter, while a
    /// host that is genuinely not allowlisted must be. Without the negative control, a runner with no
    /// egress at all would let the positive half pass for the wrong reason.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task OAuthAgent_IsNotConfined_AndItsProviderHostIsStillCarriedByTheProxy()
    {
        await using var world = await ConfinementWorld.StartAsync(new BudgetCaps(0, 0, 0, 0));

        // The OAuth shape: no API key at all, and a credentialPaths restore (the login the harvest
        // put in the host keychain coming back into a fresh tmpfs home).
        var jail = await world.LaunchAsync(
            "oauth-agent-1",
            modelApiKey: null,
            cliCredentials: new[]
            {
                new SandboxCredentialFile(".claude/.credentials.json", "{\"oauth\":\"restored\"}"u8.ToArray()),
            });

        // Nothing was confined: no token, no base-URL override, no gateway involvement whatsoever.
        var secrets = await world.ReadSecretsFileAsync(jail);
        Assert.DoesNotContain(BaseUrlVar, secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("mg_sess_", secrets, StringComparison.Ordinal);
        Assert.Null(world.Daemon.Services.GetRequiredService<AgentGatewayCredentials>()
            .UpstreamHostFor(jail.AgentId));

        // The login state really landed in the jail — this is the path that would break if the OAuth
        // agent were accidentally routed through the gateway.
        var restored = await world.ExecAsync(jail, "cat \"$HOME/.claude/.credentials.json\"");
        Assert.Contains("restored", restored, StringComparison.Ordinal);

        // And its provider host is still carried by the proxy, with a negative control proving the
        // filter is live rather than absent.
        var allowed = await world.ProxyVerdictAsync(jail, "https://api.anthropic.com/v1/messages");
        var refused = await world.ProxyVerdictAsync(jail, "https://not-allowlisted.mainguard.invalid/x");

        Assert.False(allowed.Filtered, $"the OAuth CLI's provider host was refused by our own proxy: {allowed.Detail}");
        Assert.True(refused.Filtered, $"the default-deny filter is not in effect: {refused.Detail}");
    }

    // ---------------------------------------------------------------------------------------------
    // 4. What makes "gateway on by default" safe.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The gateway is now enabled by default, on an address the daemon picks. If that address turns out
    /// not to be dialable from a jail's egress proxy, confinement must NOT engage: the agent keeps the
    /// raw key and the direct route it had before the gateway existed, and it keeps working.
    ///
    /// <para>Without this the default would be a footgun of the worst shape — the CLI is pointed at an
    /// endpoint its only route refuses, so every model call dies as a proxy error indistinguishable
    /// from a provider outage. The probe is what converts "we guessed the address right" from an
    /// assumption into a checked precondition, and it is measured here against a real proxy dialing a
    /// real unroutable private address.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task GatewayUnreachableFromTheProxy_SkipsConfinement_SoTheAgentKeepsWorking()
    {
        // A private address (so it is a shape the auto-resolution could genuinely produce) with no route
        // from a container: the connect fails rather than being refused by policy.
        const string Unreachable = "10.255.255.1:5251";

        var vmRoot = NewScratchDir("mainguard-mg4-unreach-vm-");
        var sourceRepo = NewScratchDir("mainguard-mg4-unreach-src-");
        var registryDir = ConfinementWorld.NewAdapterRegistryDir("mainguard-mg4-unreach-adapters-");
        ConfinementWorld.SeedRepoAt(sourceRepo);
        ConfinementWorld.WriteAdapterMarkerIn(registryDir);

        using var docker = new DockerClientConfiguration().CreateClient();
        var environment = new Wsl2AgentEnvironment(vmRoot: vmRoot, gatewayEndpoint: Unreachable);
        var launcher = new SandboxAgentLauncher(
            environment,
            new InstalledAdapterCatalog(registryDir),
            loggerFactory: null,
            credentials: new AgentGatewayCredentials(),
            gatewayOptions: new GatewayConfinementOptions("http://" + Unreachable, Enabled: true));

        const string agentId = "byok-unreachable-1";
        var repoHash = environment.Repos.Provision(sourceRepo).RepoHash;
        SandboxLaunchResult? launch = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            launch = await launcher.TryLaunchAsync(
                repoHash, agentId, agentKind: AdapterId, modelApiKey: RealKey, ipcDirPath: null, ct: cts.Token);
            Assert.NotNull(launch);

            var exec = await environment.Sandboxes.ExecAsync(
                launch!.ContainerId, new[] { "sh", "-c", "cat /run/secrets/agent.env" }, CancellationToken.None);
            var secrets = exec.Stdout ?? string.Empty;

            // Fell back to exactly the pre-gateway behaviour: the real key, and no redirection.
            Assert.Contains(ApiKeyVar + "=" + RealKey, secrets, StringComparison.Ordinal);
            Assert.DoesNotContain(BaseUrlVar, secrets, StringComparison.Ordinal);
            Assert.DoesNotContain("mg_sess_", secrets, StringComparison.Ordinal);

            // And the probe itself really said "no" — a positive control on the same live proxy proves
            // the probe is capable of saying "yes", so the result above is not just a broken prober.
            Assert.False(await environment.Egress.CanProxyReachAsync(Unreachable));
            Assert.True(await environment.Egress.CanProxyReachAsync(
                $"127.0.0.1:{EgressProxyConfigurator.ProxyPort}"));
        }
        finally
        {
            if (launch is not null)
            {
                try { await launcher.TeardownAsync(repoHash, agentId, launch.ContainerId, CancellationToken.None); }
                catch { /* best effort */ }
            }

            await ConfinementWorld.RemoveEgressForAsync(docker, repoHash, agentId);
            foreach (var dir in new[] { vmRoot, sourceRepo, Path.GetDirectoryName(registryDir)! })
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private static string NewScratchDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ValueOf(string envFile, string name) =>
        envFile.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))
            ?.Substring(name.Length + 1)
        ?? throw new Xunit.Sdk.XunitException($"'{name}' is not in the jail's env file:\n{envFile}");

    // =============================================================================================
    // The world: a real daemon with a real gateway listener, a real jail, and a fake provider.
    // =============================================================================================

    private sealed class ConfinementWorld : IAsyncDisposable
    {
        private readonly List<(string RepoHash, string AgentId, string ContainerId)> _jails = new();
        private readonly List<string> _tempDirs = new();
        private readonly FakeModelEndpoint _provider;
        private readonly string _vmRoot;
        private readonly string _sourceRepo;
        private readonly string _registryDir;

        private ConfinementWorld(
            FakeModelEndpoint provider, string vmRoot, string sourceRepo, string registryDir)
        {
            _provider = provider;
            _vmRoot = vmRoot;
            _sourceRepo = sourceRepo;
            _registryDir = registryDir;
            // The registry's PARENT is the adapters root this world created, so that is what gets
            // removed — deleting only the registry would leave the root behind on every run.
            _tempDirs.AddRange(new[] { vmRoot, sourceRepo, Path.GetDirectoryName(registryDir)! });
        }

        public TestDaemonHost.RunningDaemon Daemon { get; private set; } = null!;

        public IDockerClient Docker { get; } = new DockerClientConfiguration().CreateClient();

        public RecordingUpstream Upstream { get; } = new();

        public string GatewayBaseUrl { get; private set; } = string.Empty;

        public string RepoHash { get; private set; } = string.Empty;

        /// <summary>The adapters root this world created — what the jail must bind-mount.</summary>
        public string AdaptersRoot => Path.GetDirectoryName(_registryDir)!;

        public static async Task<ConfinementWorld> StartAsync(BudgetCaps caps)
        {
            var provider = new FakeModelEndpoint();
            var world = new ConfinementWorld(
                provider,
                NewTempDir("mainguard-mg4-vm-"),
                NewTempDir("mainguard-mg4-src-"),
                NewAdapterRegistryDir("mainguard-mg4-adapters-"));

            try
            {
                await world.BootAsync(caps);
                return world;
            }
            catch
            {
                await world.DisposeAsync();
                throw;
            }
        }

        private async Task BootAsync(BudgetCaps caps)
        {
            SeedRepoAt(_sourceRepo);
            WriteAdapterMarkerIn(_registryDir);
            _provider.EnqueueOk(ProviderBody).EnqueueOk(ProviderBody).EnqueueOk(ProviderBody);

            // The gateway must bind an address the egress proxy container can dial. Loopback cannot be
            // it — a container's 127.0.0.1 is the container — so this is the daemon host's own private
            // address, which is the row the oauth-budgeting measurements call reachable from an
            // egress-capable container. GatewayBindPolicy permits exactly this class of address.
            var bind = PrivateHostAddress();

            // The gateway endpoint must be known BEFORE the host starts, not after: the daemon resolves
            // IAgentEnvironment during startup, and a substrate built with a null endpoint renders a
            // proxy filter with no entry for the gateway — which is exactly the refusal under test, and
            // it fails as a 403 that looks like a provider outage. TestDaemonHost leases the control
            // port and then the gateway port per attempt, so the endpoint is filled in from the lease
            // itself and CHECKED against the port the daemon reports afterwards.
            string? gatewayEndpoint = null;
            var leases = 0;

            int Lease()
            {
                var port = TestPorts.Lease();
                if (++leases % 2 == 0)
                {
                    gatewayEndpoint = $"{bind}:{port}";   // second lease of the attempt = the gateway
                }

                return port;
            }

            Daemon = await TestDaemonHost.StartAsync(
                new DaemonOptions
                {
                    LocalDev = true,
                    TokenPath = TestDaemonHost.TempTokenPath("mainguard-mg4-tok"),
                    GatewayBindAddress = bind,
                },
                Lease,
                CancellationToken.None,
                services =>
                {
                    // An isolated VM root + the gateway address the daemon really bound.
                    services.AddSingleton<IAgentEnvironment>(_ =>
                        new Wsl2AgentEnvironment(vmRoot: _vmRoot, gatewayEndpoint: gatewayEndpoint));

                    // An adapter registry containing exactly one installed CLI, declaring the MG-4
                    // confinement pair. The real VM registry is never touched.
                    services.AddSingleton(new InstalledAdapterCatalog(_registryDir));

                    // The caps this scenario needs, replacing the boot-from-store registration.
                    services.AddSingleton(sp => new BudgetLedger(
                        sp.GetRequiredService<ISpendStore>(), () => DateTimeOffset.UtcNow, caps));

                    // The only faked leg: the daemon's transport to the provider.
                    services.AddSingleton(sp => new GatewayForwarder(
                        sp.GetRequiredService<AiGateway>(),
                        new HttpMessageInvoker(Upstream.CreateHandler(_provider.BaseAddress)),
                        delay: (_, _) => Task.CompletedTask));
                });

            // The endpoint the substrate was built with must be the port the daemon actually bound. If
            // these ever diverge the proxy permits one address while the jail is pointed at another, and
            // the only symptom is a 403 from our own proxy — so it is checked, not assumed.
            Assert.Equal($"{bind}:{Daemon.GatewayPort}", gatewayEndpoint);
            GatewayBaseUrl = "http://" + gatewayEndpoint;

            var environment = Daemon.Services.GetRequiredService<IAgentEnvironment>();
            RepoHash = environment.Repos.Provision(_sourceRepo).RepoHash;
        }

        /// <summary>Spawns a jail through the daemon's own launcher — the shipped spawn chain.</summary>
        public async Task<Jail> LaunchAsync(
            string agentId, string? modelApiKey,
            IReadOnlyList<SandboxCredentialFile>? cliCredentials = null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var launcher = Daemon.Services.GetRequiredService<SandboxAgentLauncher>();

            var launch = await launcher.TryLaunchAsync(
                RepoHash, agentId, agentKind: AdapterId, modelApiKey: modelApiKey,
                ipcDirPath: null, ct: cts.Token, extraEnv: null, cliCredentials: cliCredentials);

            Assert.NotNull(launch);
            _jails.Add((RepoHash, agentId, launch!.ContainerId));
            return new Jail(agentId, launch.ContainerId);
        }

        public Task<string> ReadSecretsFileAsync(Jail jail) => ExecAsync(jail, "cat /run/secrets/agent.env");

        /// <summary>Runs a shell line inside the jail, as the agent uid, with the container's own env.</summary>
        public async Task<string> ExecAsync(Jail jail, string script)
        {
            var environment = Daemon.Services.GetRequiredService<IAgentEnvironment>();
            var result = await environment.Sandboxes.ExecAsync(
                jail.ContainerId, new[] { "sh", "-c", script }, CancellationToken.None);
            return result.Stdout ?? string.Empty;
        }

        /// <summary>The confined CLI's request, sourced and routed exactly as the CLI would.</summary>
        public Task<string> CurlFromJailAsync(Jail jail, string curl) =>
            ExecAsync(jail, "set -a; . /run/secrets/agent.env; set +a; " + curl);

        public Task<string> PostFromJailAsync(Jail jail) => CurlFromJailAsync(
            jail,
            "curl -sS -o /dev/null -w '%{http_code}' -X POST \"$" + BaseUrlVar + "/v1/messages\" "
            + "-H \"x-api-key: $" + ApiKeyVar + "\" -H 'content-type: application/json' "
            + "-d '{\"model\":\"claude-test\",\"max_tokens\":16}'");

        /// <summary>
        /// Whether the egress proxy REFUSED a destination by policy, as distinct from failing to reach
        /// it. tinyproxy answers a filtered host with 403 and its own "Filtered" page, so the verdict is
        /// readable without any network beyond the proxy itself.
        /// </summary>
        public async Task<(bool Filtered, string Detail)> ProxyVerdictAsync(Jail jail, string url)
        {
            var output = await ExecAsync(
                jail,
                "rm -f /tmp/verdict; "
                + $"curl -sS -o /tmp/verdict -w 'code=%{{http_code}}' --max-time 25 '{url}' 2>&1; "
                + "echo; head -c 300 /tmp/verdict 2>/dev/null");

            // A REFUSED destination is a proxy verdict, and the shape differs by scheme: tinyproxy
            // answers a filtered CONNECT with `curl: (56) CONNECT tunnel failed, response 403` and a
            // filtered plain-HTTP request with a 403 carrying its own "Filtered"/"Access Violation"
            // page. Anything else — a real status, a timeout, a connect failure — is NOT a refusal, and
            // that distinction is what keeps this honest on a runner with no internet: an unreachable
            // allowlisted host still reads as "not filtered".
            var filtered = output.Contains("CONNECT tunnel failed, response 403", StringComparison.Ordinal)
                           || output.Contains("Filtered", StringComparison.OrdinalIgnoreCase)
                           || output.Contains("Access Violation", StringComparison.OrdinalIgnoreCase)
                           || output.Contains("code=403", StringComparison.Ordinal);
            return (filtered, output.Replace('\n', ' '));
        }

        public async ValueTask DisposeAsync()
        {
            var launcher = Daemon is null ? null : Daemon.Services.GetService<SandboxAgentLauncher>();
            foreach (var (repoHash, agentId, containerId) in _jails)
            {
                try
                {
                    if (launcher is not null)
                    {
                        await launcher.TeardownAsync(repoHash, agentId, containerId, CancellationToken.None);
                    }
                }
                catch { /* cleanup must never mask the assertion */ }

                try
                {
                    await Docker.Containers.RemoveContainerAsync(
                        containerId, new ContainerRemoveParameters { Force = true });
                }
                catch { /* already gone */ }
            }

            if (Daemon is not null)
            {
                await Daemon.DisposeAsync();
            }

            _provider.Dispose();
            await RemoveEgressAsync(Docker, _jails);
            Docker.Dispose();
            foreach (var dir in _tempDirs)
            {
                TryDelete(dir);
            }
        }

        /// <summary>
        /// The daemon host's own private (RFC1918) IPv4 — the address class a container on the egress
        /// network can dial back to the host. Loopback is deliberately not a candidate: it is reachable
        /// from the test process and from nothing else, so a gateway bound there would pass an
        /// in-process assertion and fail every real jail.
        /// </summary>
        private static string PrivateHostAddress()
        {
            var candidates =
                from nic in NetworkInterface.GetAllNetworkInterfaces()
                where nic.OperationalStatus == OperationalStatus.Up
                      && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                from address in nic.GetIPProperties().UnicastAddresses
                where address.Address.AddressFamily == AddressFamily.InterNetwork
                let bytes = address.Address.GetAddressBytes()
                where bytes[0] == 10
                      || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                      || (bytes[0] == 192 && bytes[1] == 168)
                select address.Address.ToString();

            var chosen = candidates.FirstOrDefault();
            Assert.False(
                chosen is null,
                "no private IPv4 address on this host, so the model gateway cannot be bound anywhere a "
                + "jail's egress proxy could reach it. The gateway is unusable in this network posture.");
            return chosen!;
        }

        internal static void WriteAdapterMarkerIn(string registryDir)
        {
            var marker = new InstalledAdapterMarker(
                AdapterId, "2.1.218", new[] { "/opt/mainguard/adapters/bin/claude" },
                ApiKeyEnvVar: ApiKeyVar,
                EgressHosts: new[] { "platform.claude.com" },
                CredentialPaths: new[] { ".claude/.credentials.json", ".claude.json" },
                BaseUrlEnvVar: BaseUrlVar,
                ModelHost: UpstreamHost);

            File.WriteAllText(
                Path.Combine(registryDir, AdapterId + ".json"), InstalledAdapterMarker.Serialize(marker));
        }

        internal static void SeedRepoAt(string path)
        {
            Repository.Init(path);
            using var repo = new Repository(path);
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            File.WriteAllText(Path.Combine(path, "README.md"), "seed\n");
            Commands.Stage(repo, "README.md");
            var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
            repo.Commit("seed commit", sig, sig);
        }

        internal static Task RemoveEgressForAsync(IDockerClient docker, string repoHash, string agentId) =>
            RemoveEgressAsync(docker, new[] { (repoHash, agentId, string.Empty) });

        private static async Task RemoveEgressAsync(
            IDockerClient docker, IEnumerable<(string RepoHash, string AgentId, string ContainerId)> jails)
        {
            var egress = new EgressProxyConfigurator(
                docker, EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog()));
            foreach (var (repoHash, agentId, _) in jails)
            {
                try { await egress.RemoveAgentSegmentAsync(repoHash, agentId); } catch { /* best effort */ }
            }

            try
            {
                await docker.Containers.RemoveContainerAsync(
                    EgressProxyConfigurator.ProxyContainerName, new ContainerRemoveParameters { Force = true });
            }
            catch { /* best effort */ }

            foreach (var name in new[]
                     {
                         EgressProxyConfigurator.AgentNetworkName, EgressProxyConfigurator.EgressNetworkName,
                     })
            {
                try
                {
                    var matches = await docker.Networks.ListNetworksAsync(new NetworksListParameters
                    {
                        Filters = new Dictionary<string, IDictionary<string, bool>>
                        {
                            ["name"] = new Dictionary<string, bool> { [name] = true },
                        },
                    });
                    foreach (var net in matches.Where(n => n.Name == name))
                    {
                        await docker.Networks.DeleteNetworkAsync(net.ID);
                    }
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// A registry dir INSIDE a real adapters root, because the spawn path bind-mounts the ROOT
        /// (the registry's parent) read-only into the jail. A bare temp dir would name a root that
        /// does not exist and Docker refuses the container outright — which is exactly how CI caught
        /// this: it passes on a box that happens to have /home/mainguard/mainguard/adapters and fails
        /// on one that does not.
        /// </summary>
        internal static string NewAdapterRegistryDir(string prefix)
        {
            var registry = Path.Combine(NewTempDir(prefix), "registry");
            Directory.CreateDirectory(registry);
            return registry;
        }

        private static string NewTempDir(string prefix)
        {
            // Path.GetTempPath() is ext4 on the Linux leg; ContainerSpecBuilder's G-11 gate refuses a
            // /mnt/<drive> source outright.
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch { /* never fail a test from cleanup */ }
        }
    }

    private sealed record Jail(string AgentId, string ContainerId);

    /// <summary>
    /// The daemon's provider transport, replaced: records what the daemon actually sent upstream (so the
    /// key-substitution claim is checked at the network boundary rather than inferred) and answers from
    /// the local <see cref="FakeModelEndpoint"/>.
    /// </summary>
    private sealed class RecordingUpstream
    {
        private readonly List<UpstreamRequest> _requests = new();
        private readonly object _gate = new();

        public IReadOnlyList<UpstreamRequest> Requests
        {
            get { lock (_gate) { return _requests.ToArray(); } }
        }

        public HttpMessageHandler CreateHandler(Uri fakeProvider) => new Handler(this, fakeProvider);

        private void Record(HttpRequestMessage request)
        {
            var headers = request.Headers
                .SelectMany(h => h.Value.Select(v => v))
                .ToArray();
            lock (_gate)
            {
                _requests.Add(new UpstreamRequest(
                    request.RequestUri!.Host,
                    request.RequestUri.Scheme,
                    request.Headers.TryGetValues("x-api-key", out var key) ? key.FirstOrDefault() : null,
                    headers));
            }
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly RecordingUpstream _owner;
            private readonly Uri _fakeProvider;
            private readonly HttpMessageInvoker _inner = new(new SocketsHttpHandler());

            public Handler(RecordingUpstream owner, Uri fakeProvider)
            {
                _owner = owner;
                _fakeProvider = fakeProvider;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _owner.Record(request);

                // Re-address to the loopback fake, keeping method, path and body. Everything the
                // middleware decided about this request is preserved.
                var rewritten = new HttpRequestMessage(request.Method,
                    new UriBuilder(_fakeProvider) { Path = request.RequestUri!.AbsolutePath }.Uri)
                {
                    Content = request.Content,
                };
                return await _inner.SendAsync(rewritten, cancellationToken);
            }
        }
    }

    private sealed record UpstreamRequest(
        string Host, string Scheme, string? ApiKeyHeader, IReadOnlyList<string> AllHeaderValues);
}
