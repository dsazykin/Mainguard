using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-07 RequiresDocker egress matrix — each row its own test (P2-07 §4). Proves default-deny with
/// the iptables backstop (not proxy-env alone), pinned DNS (NXDOMAIN for exfil names), fast refusal
/// (not timeout), a live <c>devbox add</c>, and A6 (a direct git-host clone from the agent fails fast
/// because the git host is absent from the agent allowlist). Gated on Docker availability.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class SandboxEgressDockerTests
{
    // A non-allowlisted destination must be refused within this budget — refused, not hung.
    private static readonly TimeSpan FastFailBudget = TimeSpan.FromSeconds(5);

    [RequiresDockerFact]
    public async Task Curl_AllowlistedModelApi_ShouldSucceedViaProxy()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // A 200/401 both prove the connection reached the API through the proxy (auth aside).
        var result = await fx.ExecAsync(handle.ContainerId,
            "curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "https://api.anthropic.com/v1/models");
        var code = result.Stdout.Trim();

        // `000` means curl never got an HTTP response, and on its own it cannot tell you WHICH leg
        // broke: the jail→proxy hop, or the proxy→internet hop. That ambiguity is the same diagnostic
        // weakness that cost several rounds elsewhere, so the failure now answers it before you ask.
        if (code == "000" || !System.Text.RegularExpressions.Regex.IsMatch(code, @"^\d{3}$"))
        {
            Assert.Fail(await DescribeEgressBreakAsync(fx, handle.ContainerId, result));
        }
    }

    /// <summary>
    /// Splits an egress failure into "the jail could not reach the proxy" versus "the proxy could not
    /// reach the internet", and reports the proxy's own health alongside. Every probe is best-effort:
    /// a diagnostic that throws while diagnosing replaces a real failure with a useless one.
    /// </summary>
    private static async Task<string> DescribeEgressBreakAsync(
        SandboxFixture fx, string jailId, SandboxExecResult curl)
    {
        var report = new System.Text.StringBuilder();
        report.Append("egress through the proxy failed. curl: exit=").Append(curl.ExitCode)
              .Append(" code='").Append(curl.Stdout.Trim()).Append("' stderr='").Append(curl.Stderr.Trim()).Append("'\n");

        var proxy = EgressProxyConfigurator.ProxyContainerName;

        async Task ProbeAsync(string label, string containerId, params string[] cmd)
        {
            try
            {
                var r = await fx.Engine.ExecAsync(containerId, cmd);
                report.Append("  ").Append(label).Append(": exit=").Append(r.ExitCode)
                      .Append(" '").Append(r.Stdout.Trim().Replace("\n", " | ")).Append('\'');
                if (!string.IsNullOrWhiteSpace(r.Stderr))
                {
                    report.Append(" err='").Append(r.Stderr.Trim()).Append('\'');
                }

                report.Append('\n');
            }
            catch (Exception ex)
            {
                report.Append("  ").Append(label).Append(": probe failed — ").Append(ex.Message).Append('\n');
            }
        }

        // Which PHASE of the request got furthest. This is the fact that separates "the hop never
        // happened" from "the tunnel was up and the transfer died", and without it the two are
        // indistinguishable from a bare exit code — which is how a CURLE_PARTIAL_FILE (18) came to be
        // investigated as a connectivity failure. http_connect is the CONNECT tunnel's own status:
        // 200 means the proxy established the tunnel to the upstream, so everything after that is a
        // transfer-side problem and no amount of staring at leg 1 will explain it.
        await ProbeAsync("request phases (curl)", jailId,
            "curl", "-sS", "-m", "15", "-o", "/dev/null",
            "-w", "http_connect=%{http_connect} http_code=%{http_code} num_connects=%{num_connects} "
                + "time_connect=%{time_connect} time_appconnect=%{time_appconnect} "
                + "time_starttransfer=%{time_starttransfer} size_download=%{size_download}",
            "https://api.anthropic.com/v1/models");

        // LEG 1 — jail → proxy. Name resolution and the TCP hop, separately.
        await ProbeAsync("jail resolves the proxy name", jailId, "getent", "hosts", proxy);
        var hop = await fx.TcpProbeAsync(jailId, $"{proxy}:{EgressProxyConfigurator.ProxyPort}");
        report.Append("  jail reaches proxy:").Append(EgressProxyConfigurator.ProxyPort)
              .Append(": ").Append(hop.Detail).Append('\n');

        // LEG 2 — the proxy's own health and its route out.
        await ProbeAsync("proxy daemon status", proxy, "sh", "-c",
            "echo dns=$(cat /run/mainguard/dnsmasq.status 2>&1) tinyproxy=$(cat /run/mainguard/tinyproxy.status 2>&1)");
        await ProbeAsync("proxy listeners", proxy, "sh", "-c",
            "awk 'NR>1{split($2,a,\":\"); if(a[2]==\"22B8\" && $4==\"0A\") print \"tinyproxy LISTENING\"}' /proc/net/tcp");

        // `getent ahosts`, NOT `getent hosts`. They answer different questions, and the difference has
        // already misdirected one investigation. `getent hosts` resolves AF_INET6 FIRST and reports the
        // AAAA record whenever one exists — even in a container with no IPv6 address and no IPv6 route,
        // verified against this exact image: a name with both an A and an AAAA in /etc/hosts reports
        // the AAAA under `getent hosts` and the A under `getent ahosts`. tinyproxy resolves with
        // getaddrinfo(AF_UNSPEC) — which is what `ahosts` shows — so `hosts` was reporting an address
        // the proxy would never dial and inviting an IPv6 diagnosis that the evidence does not support.
        await ProbeAsync("proxy resolves upstream (getaddrinfo order, as tinyproxy sees it)", proxy,
            "sh", "-c", "getent ahosts api.anthropic.com | grep STREAM || echo '(no STREAM result)'");
        await ProbeAsync("proxy IPv6 addresses (blank => IPv4-only fabric)", proxy,
            "sh", "-c", "grep -v '^00000000000000000000000000000001' /proc/net/if_inet6 || echo '(none — loopback only)'");
        await ProbeAsync("proxy logs", proxy, "sh", "-c", "tail -5 /run/mainguard/dnsmasq.log 2>&1");

        report.Append("Read it this way. FIRST look at http_connect: 200 means the CONNECT tunnel was "
                    + "established, so legs 1 and 2 both worked and the failure is in the transfer "
                    + "(curl 18 = CURLE_PARTIAL_FILE — the stream ended mid-response); blank/000 means "
                    + "the tunnel was never built, and then leg 1 UNREACHABLE => the jail cannot reach "
                    + "the proxy (a recreated proxy strands running jails — same IP, new MAC), while "
                    + "leg 1 REACHABLE with leg 2 broken => the proxy is up and the upstream is "
                    + "unreachable from this runner.");
        return report.ToString();
    }

    [RequiresDockerFact]
    public async Task Curl_NonAllowlistedDomain_ShouldFailFast_RefusedNotTimeout()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var sw = Stopwatch.StartNew();
        var result = await fx.ExecAsync(handle.ContainerId,
            "curl", "-sS", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "https://example.com");
        sw.Stop();

        Assert.NotEqual(0, result.ExitCode);       // refused
        Assert.True(sw.Elapsed < FastFailBudget, $"expected fast refusal, took {sw.Elapsed}");
    }

    [RequiresDockerFact]
    public async Task DirectIpEgress_ShouldBeDropped_DespiteProxyEnvUnset()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // Bypass HTTP_PROXY entirely and dial a raw IP: the iptables backstop must DROP it.
        var result = await fx.ExecAsync(handle.ContainerId,
            "env", "-u", "HTTP_PROXY", "-u", "HTTPS_PROXY", "-u", "http_proxy", "-u", "https_proxy",
            "curl", "-sS", "-m", "8", "-o", "/dev/null", "http://1.1.1.1");
        Assert.NotEqual(0, result.ExitCode);
    }

    // MG-7 — this test used to query `secret-data.attacker.tld`, which NXDOMAINs on ANY resolver on
    // earth. It therefore passed with ZERO DNS pinning in place: the jail was on Docker's embedded
    // 127.0.0.11 (forwarding to the VM's upstream DNS) and the NXDOMAIN-pinned dnsmasq existed only
    // inside the proxy container, consulted by nothing. The probe must use a name that a real resolver
    // WOULD answer, so that an answer is proof the query left through an unpinned resolver.
    [RequiresDockerFact]
    public async Task DnsExfil_ResolvableNonAllowlistedName_ShouldNotLeaveTheJail()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var result = await fx.ExecAsync(handle.ContainerId, "getent", "hosts", "example.com");

        // dnsmasq's catch-all answers 0.0.0.0 for anything not allowlisted; a resolver that actually
        // reached the internet returns example.com's real (routable) address instead. Either the lookup
        // fails outright or it is answered by the sinkhole — a routable answer means the pin is off.
        var answer = result.Stdout.Trim();
        Assert.True(
            result.ExitCode != 0 || answer.StartsWith("0.0.0.0", StringComparison.Ordinal),
            $"example.com resolved to a real address inside the jail — the query left through an unpinned "
            + $"resolver, so pinned DNS is not in the resolution path. getent said: '{answer}'");
    }

    // The structural half of MG-7, read off the live container.
    //
    // NOTE ON WHAT IS *NOT* ASSERTED: not `/etc/resolv.conf` naming the proxy. On a user-defined
    // network Docker ALWAYS writes `nameserver 127.0.0.11` into the container's resolv.conf and routes
    // HostConfig.Dns in as that embedded resolver's UPSTREAM ("ExtServers: [<proxy ip>]"). So the
    // textual assertion is unsatisfiable by construction no matter how correct the pin is — an earlier
    // revision of this test asserted it and failed against real Docker for that reason alone. The
    // control still holds: Docker's resolver answers container names from its own tables and forwards
    // everything else to our dnsmasq, and the agent network is Internal so no other resolver is
    // reachable. What is asserted here is therefore the pin itself (on the container's HostConfig)
    // plus, in the two tests below, the behaviour that only a live pinned dnsmasq can produce.
    [RequiresDockerFact]
    public async Task JailResolver_ShouldBePinnedAtTheProxy()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var proxyAddress = await fx.Egress.ResolveProxyAddressAsync();
        Assert.False(string.IsNullOrEmpty(proxyAddress), "the egress proxy has no address on the agent network");

        var inspect = await fx.InspectAsync(handle.ContainerId);
        var pinned = Assert.Single(inspect.HostConfig.DNS);
        Assert.Equal(proxyAddress, pinned);

        // And the pin is load-bearing rather than decorative: dnsmasq is actually up behind it. A dead
        // dnsmasq (the state this container shipped in before MG-25 granted it the capabilities it
        // needs to start) would leave the pin pointing at nothing.
        var status = await fx.Engine.ExecAsync(
            EgressProxyConfigurator.ProxyContainerName, new[] { "cat", "/run/mainguard/dnsmasq.status" });
        Assert.Equal("ok", status.Stdout.Trim());
    }

    /// <summary>
    /// Non-vacuity in the other direction: a pin that resolves NOTHING would also pass the exfil probe
    /// above while breaking every agent. The allowlisted model API must still resolve — <b>and it must
    /// do so without a public resolver</b>.
    ///
    /// <para><b>What this used to be, and why it could flake.</b> It asked <c>getent hosts</c> and
    /// checked the answer was not 0.0.0.0, against a dnsmasq that forwarded every allowlisted domain to
    /// a hardcoded <c>1.1.1.1</c>. Both halves were wrong. The upstream made the test — and in-jail DNS
    /// generally — fail anywhere an external resolver is blocked, intercepted or unreachable, which is
    /// a real defect this test carried rather than caught. And <c>getent</c> is the wrong instrument:
    /// glibc short-circuits (it never issues an AAAA query on an IPv4-only host) and <c>getent
    /// hosts</c> specifically resolves AF_INET6 first, so it reports things the jail's own resolvers
    /// never see. The agent CLIs are Node and Go binaries carrying their own resolvers, so the
    /// assertion is made through one that actually puts the query on the wire.</para>
    ///
    /// <para><b>The two setup steps are load-bearing, both measured against the real image.</b> Blocking
    /// 1.1.1.1 in the proxy's netns is what makes "resolution does not depend on a public resolver" an
    /// assertion instead of a hope. Flushing dnsmasq's cache (SIGHUP) is what makes the block bite: with
    /// a warm cache the same probe answers correctly with 1.1.1.1 fully blocked and the fix removed —
    /// i.e. it passes vacuously. Verified both ways against <c>mainguard-egress-proxy:latest</c>: with a
    /// cold cache and 1.1.1.1 dropped, the old <c>server=/…/1.1.1.1</c> config answers REFUSED (EDE:
    /// network error) and this config resolves normally.</para>
    ///
    /// <para>The DROP rule is removed in a <c>finally</c>, and it is self-healing besides: the backstop
    /// is applied with <c>iptables-restore</c> over the whole <c>*filter</c> table, so the next config
    /// push replaces it regardless.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task AllowlistedName_ShouldStillResolve_WithoutAnyPublicResolver()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var proxy = EgressProxyConfigurator.ProxyContainerName;
        const string publicResolver = "1.1.1.1";

        var blocked = await fx.ExecAsync(proxy, "iptables", "-I", "OUTPUT", "-d", publicResolver, "-j", "DROP");
        Assert.True(blocked.ExitCode == 0,
            $"could not block {publicResolver} in the proxy netns, so this test would prove nothing: "
            + $"exit={blocked.ExitCode} stderr='{blocked.Stderr.Trim()}'");

        try
        {
            // Cold cache, or the block is unobservable — see the summary.
            var flushed = await fx.ExecAsync(proxy, "pkill", "-HUP", "-x", "dnsmasq");
            Assert.True(flushed.ExitCode == 0,
                $"could not flush dnsmasq's cache, so a warm entry could answer this probe without any "
                + $"upstream being reached: exit={flushed.ExitCode} stderr='{flushed.Stderr.Trim()}'");

            const string host = "api.anthropic.com";
            var v4 = await fx.ExecAsync(handle.ContainerId, "node", "-e",
                $"require('dns').resolve4('{host}',(e,a)=>console.log(e?'ERR '+e.code:'A '+a.join(',')))");
            Assert.Equal(0, v4.ExitCode);

            var answer = v4.Stdout.Trim();
            Assert.True(answer.StartsWith("A ", StringComparison.Ordinal),
                $"the jail could not resolve the allowlisted '{host}' while {publicResolver} was "
                + "unreachable. dnsmasq must forward allowlisted names to the proxy container's OWN stub "
                + $"resolver (EgressProxyConfig.DockerEmbeddedResolver), never to a public one. Got: '{answer}'");

            // ...and it is a real answer, not the catch-all sinkhole.
            Assert.DoesNotContain("0.0.0.0", answer, StringComparison.Ordinal);
        }
        finally
        {
            await fx.ExecAsync(proxy, "iptables", "-D", "OUTPUT", "-d", publicResolver, "-j", "DROP");
        }
    }

    // The toolchain is PRE-BAKED into the agent image (A6 decision): devbox's runtime `add` resolves
    // nixpkgs from the git host, which the default-deny jail forbids, so the curated toolchain is Nix-
    // installed at build time into a persistent /opt/toolchain profile and is on PATH from the read-only
    // image — needing ZERO runtime egress (not even cache.nixos.org). This is the toolchain-sideload
    // edge case's real intent: tools are available in a live session and running one never severs it
    // (G-16). Adding an ARBITRARY new tool at runtime is a documented v1.x item.
    [RequiresDockerFact]
    public async Task PrebakedToolchain_ShouldBeAvailableInLiveSession()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync();

        // Every curated tool resolves on PATH inside the hardened jail (no runtime fetch — A6 intact).
        foreach (var tool in new[] { "jq", "rg", "fd", "make", "node", "python3", "go" })
        {
            var where = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "command -v " + tool);
            Assert.True(where.ExitCode == 0,
                $"pre-baked tool '{tool}' not on PATH in the sandbox.\nstdout: {where.Stdout}\nstderr: {where.Stderr}");
        }

        // Running a tool mid-session succeeds and does not sever the session (the G-16 rationale for
        // baking over a runtime image build).
        var run = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "jq --version");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("jq", run.Stdout, StringComparison.Ordinal);
    }

    // MG-18 — the regression the finding asks for: a network that already exists under the agent
    // network's name but is NOT internal. Reuse-by-name accepted it, so every jail attached to it had a
    // default route to the internet with no error reported anywhere. It must now fail closed.
    [RequiresDockerFact]
    public async Task PreexistingNonInternalAgentNetwork_ShouldRefuseToSpawn_NotSilentlyDisableEgress()
    {
        await using var fx = new SandboxFixture();

        // Clear whatever a previous test left behind, then plant the drifted network.
        await fx.ForceRemoveProxyAndNetworksAsync();
        await fx.Docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = EgressProxyConfigurator.AgentNetworkName,
            Driver = "bridge",
            Internal = false, // the drift
            Labels = new Dictionary<string, string>
            {
                [EgressProxyConfigurator.NetworkRoleLabel] = EgressProxyConfigurator.RoleFor(true),
            },
        });

        var ex = await Assert.ThrowsAsync<EgressNetworkDriftException>(() => fx.EnsureEgressReadyAsync());
        Assert.Equal(EgressProxyConfigurator.AgentNetworkName, ex.NetworkName);
    }

    // The proxy is a SHARED singleton, so it can be stopped or removed while EnsureReadyAsync is in the
    // middle of adopting it — by a fixture teardown, by the VM-shutdown path, or by Docker still
    // completing a stop when the next start lands. CI caught the last of those: a proxy that lived
    // 340ms and exited 143 (128+15, SIGTERM) with empty logs, reported as "it exited during boot".
    // Neither removal nor a signal is a failure; both must resolve by starting the sequence over.
    [RequiresDockerFact]
    public async Task EnsureReady_WhenTheProxyIsStoppedUnderneathIt_RecoversInsteadOfFailing()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        // Two interleavings, because the disturbance can land at different points and each one produced
        // its own CI failure before this was handled as a class:
        //   delay 0    — the stop races the adopt/start (CI saw exit 143, a 340ms lifetime, empty logs)
        //   delay >0   — the stop lands AFTER the readiness check, while the config exec is in flight
        //                (CI saw a raw 409 Conflict, "container ... is not running")
        // "Wait until running, then exec" is TOCTOU against a container we do not own, so the exec has
        // to survive being raced, not merely be preceded by a check.
        foreach (var delayMs in new[] { 0, 0, 120, 250, 400 })
        {
            var stop = Task.Run(async () =>
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }

                try
                {
                    await fx.Docker.Containers.StopContainerAsync(
                        EgressProxyConfigurator.ProxyContainerName, new ContainerStopParameters());
                }
                catch (Docker.DotNet.DockerApiException)
                {
                    // The proxy may legitimately be mid-recreate — the point is the disturbance, not
                    // that this particular stop lands.
                }
            });

            var ensure = fx.EnsureEgressReadyAsync();

            // The property under test is that EnsureReadyAsync RECOVERS from the disturbance instead of
            // throwing — so awaiting it without an exception is itself the assertion for this round.
            await Task.WhenAll(stop, ensure);
        }

        // A stop deliberately scheduled to land late can fire AFTER the last EnsureReadyAsync has
        // already returned, which leaves the proxy legitimately stopped — that is the racing harness,
        // not the daemon. One final uncontended call establishes the quiescent state to assert on; it
        // is not a retry of the property above, which has already been exercised five times.
        await fx.EnsureEgressReadyAsync();

        var inspect = await fx.Docker.Containers.InspectContainerAsync(EgressProxyConfigurator.ProxyContainerName);
        Assert.True(inspect.State.Running, "the proxy must end up running despite being stopped mid-adoption");

        // And it must be a WORKING proxy, not merely a running one — the recovery has to re-push policy.
        var status = await fx.Engine.ExecAsync(
            EgressProxyConfigurator.ProxyContainerName, new[] { "cat", "/run/mainguard/dnsmasq.status" });
        Assert.Equal("ok", status.Stdout.Trim());
    }

    [RequiresDockerFact]
    public async Task ProxyContainer_ShouldRunHardened_NoNetRaw_SeccompAndReadOnlyRootfs()
    {
        // MG-25 read off the live proxy: the chokepoint every agent's egress crosses carries the same
        // class of controls as the jails it fronts.
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        var inspect = await fx.Docker.Containers.InspectContainerAsync(EgressProxyConfigurator.ProxyContainerName);
        var host = inspect.HostConfig;

        Assert.Contains("ALL", host.CapDrop);
        Assert.Contains("NET_ADMIN", host.CapAdd);
        Assert.DoesNotContain("NET_RAW", host.CapAdd);
        Assert.Contains(host.SecurityOpt, o => o.StartsWith("seccomp=", StringComparison.Ordinal));
        Assert.True(host.ReadonlyRootfs);
        Assert.True(host.Memory > 0);
        Assert.True(host.NanoCPUs > 0);

        // The read-only rootfs must not have broken the proxy: both daemons are up and the policy the
        // daemon rendered is on the /run tmpfs where reload.sh reads it.
        Assert.True(inspect.State.Running);

        var filter = await fx.Engine.ExecAsync(inspect.ID, new[] { "cat", "/run/mainguard/tinyproxy-filter" });
        Assert.Equal(0, filter.ExitCode);

        // Compare against the RENDERER, not against a hand-written hostname. The filter is a list of
        // anchored regexes, so the literal "api.anthropic.com" never appears in it — the file says
        // `^api\.anthropic\.com$`. An earlier revision of this test asserted the bare hostname and
        // failed against real Docker for that reason alone, while the file was perfectly correct.
        var expected = EgressProxyConfig.RenderTinyproxyFilter(fx.Egress.Allowlist);
        Assert.Equal(expected.Trim(), filter.Stdout.Trim());

        // Both daemons came up under the read-only rootfs and the reduced capability set.
        var dns = await fx.Engine.ExecAsync(inspect.ID, new[] { "cat", "/run/mainguard/dnsmasq.status" });
        Assert.Equal("ok", dns.Stdout.Trim());
        var tp = await fx.Engine.ExecAsync(inspect.ID, new[] { "cat", "/run/mainguard/tinyproxy.status" });
        Assert.Equal("ok", tp.Stdout.Trim());
    }

    // A reload must actually REPLACE the running daemons. `pkill` was absent from the image (procps was
    // never installed) and, once dnsmasq drops privileges, killing it also needs CAP_KILL — so every
    // reload after the first silently left the ORIGINAL processes serving the ORIGINAL policy. An
    // allowlist edit, including REMOVING a host, therefore never reached a running proxy.
    [RequiresDockerFact]
    public async Task RepeatedConfigPush_ShouldRestartTheDaemons_NotLeaveStalePolicyRunning()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        // A second and third push exercise exactly the path that used to go stale.
        await fx.EnsureEgressReadyAsync();
        await fx.EnsureEgressReadyAsync();

        var proxy = EgressProxyConfigurator.ProxyContainerName;
        var dns = await fx.Engine.ExecAsync(proxy, new[] { "cat", "/run/mainguard/dnsmasq.status" });
        var tp = await fx.Engine.ExecAsync(proxy, new[] { "cat", "/run/mainguard/tinyproxy.status" });

        // "stale" is the verdict reload.sh writes when it could not stop the predecessor.
        Assert.Equal("ok", dns.Stdout.Trim());
        Assert.Equal("ok", tp.Stdout.Trim());
    }

    // P2-08 gateway fronting — the rendered `tinyproxy-upstreams` artefact must be LOADED by the
    // running proxy, not merely written. It was written on every config push and read by nothing:
    // reload.sh generated tinyproxy.conf without ever pulling the upstreams in, so every model-API
    // request went STRAIGHT to the provider with none of the gateway's token bucket, budget or
    // 429-shaping — and nothing reported it, because the file existed and its contents were correct.
    //
    // Asserting the file exists (or matching it against the renderer) reproduces the defect exactly,
    // so this asserts BEHAVIOUR. With the gateway pointed at a loopback port nothing listens on, a
    // model-API request through the proxy comes back `502 Unable to connect to upstream proxy` — a
    // response only tinyproxy with a LOADED `upstream` directive can produce. The control is a
    // non-fronted allowlisted host (PackageRegistry ⇒ direct route), which must NOT get that answer;
    // without it a proxy that was simply broken would pass.
    //
    // Deliberately needs no internet: the dead gateway refuses on loopback immediately (the backstop
    // accepts `-i lo`), and the direct route's own failure on an offline runner is tinyproxy's
    // `500 Unable to connect`, which is distinct from the upstream verdict either way.
    [RequiresDockerFact]
    public async Task RenderedGatewayUpstreams_ShouldBeInEffectOnTheRunningProxy_NotMerelyWritten()
    {
        await using var fx = new SandboxFixture();

        const string DeadGateway = "127.0.0.1:1";
        var egress = new EgressProxyConfigurator(
            fx.Docker,
            EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog()),
            gatewayUpstream: DeadGateway);

        // Start from a clean proxy: this one has to be created with the gateway-fronting config, and a
        // proxy another test left running would be adopted with ITS config still loaded.
        await fx.ForceRemoveProxyAndNetworksAsync();
        await egress.EnsureReadyAsync();

        var proxy = EgressProxyConfigurator.ProxyContainerName;

        // Sanity: tinyproxy accepted the config it was given. A tinyproxy that failed to parse the
        // appended upstreams would not be listening at all, and every assertion below would then be
        // reporting the wrong thing.
        var status = await fx.Engine.ExecAsync(proxy, new[] { "cat", "/run/mainguard/tinyproxy.status" });
        Assert.Equal("ok", status.Stdout.Trim());

        var fronted = await ProxyGetAsync(fx, proxy, "api.anthropic.com");   // ModelApi ⇒ gateway-fronted
        Assert.True(
            fronted.Contains("502", StringComparison.Ordinal)
            && fronted.Contains("upstream proxy", StringComparison.Ordinal),
            "the model-API host was NOT routed through the configured gateway: tinyproxy answered without "
            + "the upstream verdict, so the rendered tinyproxy-upstreams is being written and never loaded "
            + $"(P2-08 fronting inert). The proxy said:\n{fronted}");

        var direct = await ProxyGetAsync(fx, proxy, "registry.npmjs.org"); // PackageRegistry ⇒ direct
        Assert.DoesNotContain("upstream proxy", direct, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issues a plain HTTP proxy request from INSIDE the proxy container and returns what came back.
    /// bash's <c>/dev/tcp</c> rather than curl — the proxy image has no HTTP client, and adding one to
    /// the chokepoint container to satisfy a test would be the wrong trade.
    /// </summary>
    private static async Task<string> ProxyGetAsync(SandboxFixture fx, string proxyContainer, string host)
    {
        const string Script =
            "exec 3<>/dev/tcp/127.0.0.1/8888 || { echo PROXY-UNREACHABLE; exit 1; }; " +
            "printf 'GET http://%s/ HTTP/1.0\\r\\nHost: %s\\r\\n\\r\\n' \"$1\" \"$1\" >&3; " +
            "head -c 300 <&3";

        var result = await fx.Engine.ExecAsync(proxyContainer,
            new[] { "timeout", "25", "bash", "-c", Script, "bash", host });
        return result.Stdout + result.Stderr;
    }

    [RequiresDockerFact]
    public async Task DirectGitHostClone_FromAgent_ShouldFailFast()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // A6: the git host is deliberately absent from the agent allowlist — a direct clone fails fast.
        var sw = Stopwatch.StartNew();
        var result = await fx.ExecAsync(handle.ContainerId,
            "git", "-c", "http.lowSpeedLimit=1", "-c", "http.lowSpeedTime=5",
            "clone", "--depth", "1", "https://github.com/git/git.git", "/tmp/should-not-clone");
        sw.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"expected fast refusal, took {sw.Elapsed}");
    }
}
