using System;
using System.Linq;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-41 — a config push must not take the proxy's listeners down, and "ready" must mean the proxy will
/// still be up a moment later.
///
/// <para><b>The finding.</b> Two independent mechanisms restarted tinyproxy/dnsmasq when nothing about
/// the policy had changed. (1) <c>reload.sh</c> stopped and restarted both daemons on EVERY config push,
/// and a push runs on every agent spawn — sampling <c>/proc/net</c> every 10 ms across a reload measured
/// tinyproxy's listener down ~80 ms and dnsmasq's ~20 ms, so spawning one agent blackholed every other
/// agent's egress. (2) The image entrypoint ran its OWN <c>reload.sh</c>, unsynchronised with
/// <c>EnsureReadyAsync</c> returning: the live tinyproxy's start time matched the entrypoint's reload,
/// not the daemon's push. Locally that landed ~1.5 s before setup returned; on a slower runner it lands
/// after, i.e. a caller told "ready" could still have the proxy restart underneath it.</para>
///
/// <para><b>What the assertions read.</b> The daemons' pid files. A restart necessarily produces a new
/// pid, and a pid is a fact about the process rather than an inference from timing — no sampling window
/// to miss the outage in, and no sleep tuned to hide or reveal it. Every test here pairs the property
/// with a control that fails if the skip became unconditional, because "the pid did not change" is
/// exactly what a proxy that never reloads at all would also report.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public class EgressProxyReloadDockerTests
{
    private readonly ITestOutputHelper _output;

    public EgressProxyReloadDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>A host that is not in <see cref="EgressAllowlist.DefaultEntries"/> and is removed
    /// mid-test — the one whose reachability has to change.</summary>
    private const string RemovedHost = "example.org";

    /// <summary>A host that is not in the defaults either and stays allowlisted throughout — the control
    /// that keeps the removal assertion honest.</summary>
    private const string KeptHost = "example.com";

    // ---- (b) the entrypoint's own reload must not land after the caller is told "ready" ----

    /// <summary>
    /// After <c>EnsureReadyAsync</c> returns, nothing restarts the proxy's daemons.
    ///
    /// <para>The container is removed first so this exercises a real boot: create → start → the
    /// entrypoint's boot reload → the daemon's config push, in whatever order they happen to land. The
    /// pid is sampled the instant setup returns and again once the entrypoint has finished booting, and
    /// the window between them is closed on the entrypoint's own log line rather than on a sleep — a
    /// fixed delay would either be too short to contain the boot reload or long enough to prove nothing
    /// about when it happened.</para>
    ///
    /// <para>Non-vacuity is not theoretical here: with <c>--boot</c> removed from the entrypoint (the
    /// pre-MG-41 behaviour) this fails with a changed tinyproxy pid, because the boot reload does land
    /// after the push.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task AfterEnsureReadyReturns_TheBootReloadDoesNotRestartTheProxy()
    {
        await using var fx = new SandboxFixture();

        // A genuine cold boot — otherwise the entrypoint has long since run and there is no second
        // reload to race, which would make this pass without testing anything.
        await fx.ForceRemoveProxyAndNetworksAsync();

        await fx.EnsureEgressReadyAsync();
        var atReady = await ReadDaemonPidsAsync(fx);

        // The entrypoint prints this AFTER its boot reload has resolved, in both the pre- and post-fix
        // builds, so waiting on it bounds the window by the event instead of by the clock.
        var booted = await WaitForProxyLogAsync(fx, "boot: idle", TimeSpan.FromSeconds(30));
        Assert.True(booted, "the proxy's entrypoint never reported 'boot: idle', so the boot reload it "
            + "gates cannot be said to have happened yet and this test proves nothing.");

        var afterBoot = await ReadDaemonPidsAsync(fx);

        _output.WriteLine($"pids at ready: {atReady}; after boot: {afterBoot}");
        _output.WriteLine("proxy log:\n" + await ReadProxyLogAsync(fx));

        Assert.Equal(atReady.Tinyproxy, afterBoot.Tinyproxy);
        Assert.Equal(atReady.Dnsmasq, afterBoot.Dnsmasq);
    }

    /// <summary>
    /// The boot reload yields to a push that has already run — even when the proxy's own bookkeeping
    /// says a restart would otherwise be warranted.
    ///
    /// <para>This is the half of the fix the cold-boot test above cannot see. Two gates keep the boot
    /// reload off the daemons: it yields when any reload has already run (this test), and the general
    /// unchanged-policy gate skips a restart whose config is byte-identical to the loaded one (the next
    /// test). On a healthy boot the second one fires first, so removing the first changes nothing
    /// observable there — measured: with <c>--boot</c> dropped from the entrypoint the cold-boot test
    /// still passes, because the boot reload skips on the digest instead.</para>
    ///
    /// <para>They differ exactly where the digest gate cannot help: a push whose reload could not
    /// record what the live daemons are serving (a daemon that failed to come up, an exec killed
    /// mid-reload) leaves no applied digest, and a boot reload arriving after that would restart both
    /// daemons underneath a caller that has already been told "ready". That state is reproduced here by
    /// deleting the record — the same on-disk situation, without having to break a daemon to get it —
    /// and the control below proves the state is one a restart really would fire on.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheBootReload_YieldsToAPushThatAlreadyRan_EvenWhenARestartWouldOtherwiseFire()
    {
        await using var fx = new SandboxFixture();

        await fx.EnsureEgressReadyAsync();
        var before = await ReadDaemonPidsAsync(fx);

        // The state a push leaves when it cannot attest which bytes the live daemons parsed. Nothing may
        // be skipped on this record any more, so any reload that consults only the digest must restart.
        var cleared = await fx.ExecAsync(EgressProxyConfigurator.ProxyContainerName,
            "sh", "-c", "rm -f /run/mainguard/applied.digest && test ! -f /run/mainguard/applied.digest");
        Assert.Equal(0, cleared.ExitCode);

        // THE PROPERTY: the entrypoint's reload, invoked exactly as the entrypoint invokes it.
        var boot = await fx.ExecAsync(EgressProxyConfigurator.ProxyContainerName,
            "sh", "/etc/mainguard/reload.sh", "--boot");
        var afterBoot = await ReadDaemonPidsAsync(fx);
        _output.WriteLine($"boot reload said: {boot.Stdout.Trim()}");
        _output.WriteLine($"pids: before={before} afterBootReload={afterBoot}");

        Assert.Equal(before.Tinyproxy, afterBoot.Tinyproxy);
        Assert.Equal(before.Dnsmasq, afterBoot.Dnsmasq);

        // THE CONTROL: the very same state, reloaded as a config push, DOES restart them. Without this,
        // "the pids did not change" is equally well explained by a reload script that no longer restarts
        // anything at all — and that would be the far worse bug of the two.
        var push = await fx.ExecAsync(EgressProxyConfigurator.ProxyContainerName,
            "sh", "/etc/mainguard/reload.sh");
        var afterPush = await ReadDaemonPidsAsync(fx);
        _output.WriteLine($"push reload said: {push.Stdout.Trim()}");
        _output.WriteLine($"pids after the push: {afterPush}");

        Assert.NotEqual(afterBoot.Tinyproxy, afterPush.Tinyproxy);
        Assert.NotEqual(afterBoot.Dnsmasq, afterPush.Dnsmasq);
    }

    // ---- (a) an unchanged policy must not restart anything, and a changed one must ----

    /// <summary>
    /// A config push whose rendered policy is byte-identical to the loaded one leaves both daemons
    /// running; a push that actually changes the policy restarts them.
    ///
    /// <para>The second half is not decoration. "Never restart" and "restart only when the policy
    /// changed" are indistinguishable from the first half alone, and the first is a silent security
    /// failure: it is the pkill/CAP_KILL bug again — an allowlist edit that never reaches the running
    /// proxy. Both directions are asserted in the same test so they cannot drift apart.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task AConfigPush_RestartsTheDaemons_OnlyWhenThePolicyActuallyChanged()
    {
        await using var fx = new SandboxFixture();

        // The fixture's OWN configurator, deliberately: every push in this test then renders from one
        // allowlist. A second configurator with its own allowlist would race this one — whichever
        // pushed last would decide the policy, and a test that pushes two different policies at the same
        // proxy is measuring the interleaving, not the reload.
        await fx.EnsureEgressReadyAsync();
        var first = await ReadDaemonPidsAsync(fx);

        // THE PROPERTY: the same policy again — the shape of every agent spawn, since spawning does not
        // touch the allowlist.
        await fx.EnsureEgressReadyAsync();
        var unchanged = await ReadDaemonPidsAsync(fx);

        _output.WriteLine($"pids: first={first} afterUnchangedPush={unchanged}");
        Assert.Equal(first.Tinyproxy, unchanged.Tinyproxy);
        Assert.Equal(first.Dnsmasq, unchanged.Dnsmasq);

        // THE CONTROL: a real edit must still reach the daemons. Without this the assertions above are
        // satisfied by a proxy that ignores config pushes entirely.
        fx.Egress.Allowlist.Add(new EgressAllowlistEntry("Reload probe", RemovedHost, EgressEntryKind.Custom), "test");
        await fx.EnsureEgressReadyAsync();
        var changed = await ReadDaemonPidsAsync(fx);

        _output.WriteLine($"pids after a CHANGED policy: {changed}");
        Assert.NotEqual(unchanged.Tinyproxy, changed.Tinyproxy);
        Assert.NotEqual(unchanged.Dnsmasq, changed.Dnsmasq);
    }

    /// <summary>
    /// The one that matters: a host REMOVED from the allowlist stops being reachable through the proxy.
    ///
    /// <para>Skipping a reload is only safe if it can never leave the old policy serving, and that is
    /// precisely the bug the daemon restart was introduced to fix — a silently-failing <c>pkill</c> left
    /// the previous tinyproxy running, so a removed host stayed permitted with nothing anywhere
    /// reporting a problem. This asserts the outcome directly rather than the mechanism.</para>
    ///
    /// <para><b>The verdict is tinyproxy's 403, not "the request failed".</b> A filtered host is
    /// rejected by the filter before any connection is attempted (measured against this image: allowed →
    /// 200, filtered → 403), whereas an allowed host that cannot be reached fails as a 5xx/timeout. So
    /// 403-versus-not distinguishes "the filter denies this" from "the network is unavailable", and the
    /// assertion stays valid on a runner with no route to the public internet. A test that asserted "the
    /// request failed" would pass on any offline machine with the fix removed.</para>
    ///
    /// <para><b>The kept host is the other half.</b> It is allowlisted in both pushes, so it pins that
    /// the proxy is alive, the filter is loaded, and the probe reaches it — without it, a proxy that
    /// 403s everything (a truncated or empty filter, i.e. default-deny with no allowances) would satisfy
    /// the removal assertion perfectly.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task RemovingAHostFromTheAllowlist_StopsItBeingReachableThroughTheProxy()
    {
        await using var fx = new SandboxFixture();

        // Edited on the FIXTURE's allowlist, which is the one every push in this test renders from —
        // including the push CreateJailOnSegmentAsync makes on the way to standing up the jail. A
        // second configurator with a second allowlist loses that race: the jail's push overwrites the
        // policy under test, and the probe then reads a filter this test never wrote (measured — both
        // hosts came back 403 because neither was in the allowlist that pushed last).
        var allowlist = fx.Egress.Allowlist;
        allowlist.Add(new EgressAllowlistEntry("Kept", KeptHost, EgressEntryKind.Custom), "test");
        allowlist.Add(new EgressAllowlistEntry("Removed", RemovedHost, EgressEntryKind.Custom), "test");

        // A real jail on its own segment, dialling the proxy exactly as an agent does.
        var repo = "mg41" + Guid.NewGuid().ToString("N")[..8];
        var (jail, segment) = await fx.CreateJailOnSegmentAsync(repo, "agent-a");
        var proxy = $"{segment.ProxyAddress}:{EgressProxyConfigurator.ProxyPort}";

        var keptBefore = await ProxyStatusAsync(fx, jail, proxy, KeptHost);
        var removedBefore = await ProxyStatusAsync(fx, jail, proxy, RemovedHost);
        _output.WriteLine($"while allowlisted: {KeptHost}={keptBefore} {RemovedHost}={removedBefore}");

        Assert.NotEqual("403", removedBefore);
        Assert.NotEqual("403", keptBefore);

        // The edit, and the push that has to carry it to the running proxy.
        Assert.True(allowlist.Remove(RemovedHost, "test"));
        await fx.EnsureEgressReadyAsync();

        var keptAfter = await ProxyStatusAsync(fx, jail, proxy, KeptHost);
        var removedAfter = await ProxyStatusAsync(fx, jail, proxy, RemovedHost);
        _output.WriteLine($"after removal:    {KeptHost}={keptAfter} {RemovedHost}={removedAfter}");

        // CONTROL first: if this broke, the assertion below proves nothing about the removal.
        Assert.True(keptAfter != "403",
            $"the still-allowlisted host '{KeptHost}' is now filtered too ({keptAfter}) — the proxy is "
            + "denying everything, so the removal below is not evidence of anything. Check that the "
            + "tinyproxy filter was rendered and loaded at all.");

        Assert.True(removedAfter == "403",
            $"'{RemovedHost}' was removed from the allowlist and the proxy still does not filter it "
            + $"(HTTP {removedAfter}, was {removedBefore}) — the running tinyproxy is serving the OLD "
            + "filter, which is the pkill/CAP_KILL failure the daemon restart exists to prevent.");
    }

    // ---- helpers ----

    /// <summary>The two daemons' pids, read from the pid files reload.sh points them at. A restart
    /// necessarily changes both; nothing else does.</summary>
    private static async Task<DaemonPids> ReadDaemonPidsAsync(SandboxFixture fx)
    {
        var result = await fx.ExecAsync(EgressProxyConfigurator.ProxyContainerName, "sh", "-c",
            "echo dnsmasq=$(cat /run/mainguard/dnsmasq.pid 2>/dev/null || echo none) "
            + "tinyproxy=$(cat /run/mainguard/tinyproxy.pid 2>/dev/null || echo none)");

        var text = result.Stdout.Trim();
        var pids = new DaemonPids(Field(text, "dnsmasq="), Field(text, "tinyproxy="));

        // A pid of "none" would make every equality assertion in this file pass trivially. It means the
        // daemon never started, which is a failure in its own right.
        Assert.True(pids.Dnsmasq != "none" && pids.Tinyproxy != "none",
            $"the proxy's daemons have no pid files ('{text}'), so nothing here is measuring a restart.");
        return pids;
    }

    private static string Field(string text, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, System.Text.RegularExpressions.Regex.Escape(key) + @"(\S+)");
        return match.Success ? match.Groups[1].Value : "none";
    }

    private readonly record struct DaemonPids(string Dnsmasq, string Tinyproxy)
    {
        public override string ToString() => $"dnsmasq={Dnsmasq} tinyproxy={Tinyproxy}";
    }

    /// <summary>
    /// The HTTP status the proxy answers for <paramref name="host"/>, probed from inside a jail through
    /// the proxy (<c>-x</c>) rather than around it. <c>--max-time</c> keeps an unroutable upstream from
    /// stalling the suite; a timeout yields <c>000</c>, which is a perfectly good "not 403".
    /// </summary>
    private static async Task<string> ProxyStatusAsync(
        SandboxFixture fx, string jail, string proxyHostPort, string host)
    {
        var result = await fx.ExecAsync(jail,
            "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}",
            "--max-time", "20", "-x", "http://" + proxyHostPort, "http://" + host + "/");
        return result.Stdout.Trim();
    }

    /// <summary>Polls the proxy container's logs until <paramref name="marker"/> appears.</summary>
    private static async Task<bool> WaitForProxyLogAsync(SandboxFixture fx, string marker, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ReadProxyLogAsync(fx)).Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<string> ReadProxyLogAsync(SandboxFixture fx)
    {
        using var logs = await fx.Docker.Containers.GetContainerLogsAsync(
            EgressProxyConfigurator.ProxyContainerName,
            tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "100" });
        var (stdout, stderr) = await logs.ReadOutputToEndAsync(default);
        return stdout + stderr;
    }
}
