using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;

namespace Mainguard.Server.Tests;

/// <summary>
/// TI-P2-00 acceptance (§A.4 / plan §6 row 7): the shared-fixture contracts — DaemonFixture
/// smoke, FakeModelEndpoint replay determinism, DualRepoFixture round-trip, AuditProbe, and the
/// <see cref="DockerSuiteCollection"/> daemon lock that keeps two concurrent RequiresDocker runs off
/// each other's shared Docker state.
/// (The ScriptedAgentHarness self-test lives in Mainguard.Tests, where the harness is built.)
/// </summary>
public sealed class FixtureAcceptanceTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public FixtureAcceptanceTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task DaemonFixture_Smoke_AuthedOk_WrongTokenDenied()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());

        var ok = await client.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.NotNull(ok);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(new ListAgentsRequest(), _daemon.WrongTokenHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task FakeModelEndpoint_ShouldReplayScriptedResponses_Deterministically()
    {
        using var endpoint = new FakeModelEndpoint()
            .EnqueueOk()
            .EnqueueRateLimited(retryAfterSeconds: 5)
            .EnqueueUnauthorized();

        using var http = new HttpClient();
        var first = await http.GetAsync(endpoint.BaseAddress);
        var second = await http.GetAsync(endpoint.BaseAddress);
        var third = await http.GetAsync(endpoint.BaseAddress);

        Assert.Equal(200, (int)first.StatusCode);
        Assert.Equal(429, (int)second.StatusCode);
        Assert.Equal(5, second.Headers.RetryAfter?.Delta?.TotalSeconds);
        Assert.Equal(401, (int)third.StatusCode);
        Assert.Equal(3, endpoint.ServedCount);
    }

    [Fact]
    public void DualRepoFixture_BareMirror_ShouldMatchWorkRepoRefs()
    {
        using var dual = new DualRepoFixture();

        // A fresh commit + push keeps the quarantine mirror byte-identical to the work heads.
        dual.Commit("feature.txt", "content\n", "add feature");
        PushHead(dual);

        var work = DualRepoFixture.CaptureRefState(dual.WorkRepoPath);
        var bare = DualRepoFixture.CaptureRefState(dual.BareMirrorPath);

        Assert.NotEmpty(bare);
        foreach (var (refName, sha) in bare)
        {
            Assert.True(work.TryGetValue(refName, out var workSha) && workSha == sha,
                $"mirror ref {refName}={sha} not byte-identical in the work repo.");
        }
    }

    [Fact]
    public void AuditProbe_ShouldAssertSequenceAndExactlyOne()
    {
        var log = new InMemoryAuditLog();
        log.Append(new AuditEvent("spawn", new Dictionary<string, string> { ["agent_id"] = "a1" }));
        log.Append(new AuditEvent("plan_approved", new Dictionary<string, string> { ["agent_id"] = "a1" }));
        log.Append(new AuditEvent("merge", new Dictionary<string, string> { ["agent_id"] = "a1" }));

        var probe = new AuditProbe(log);
        probe.AssertSequence("spawn", "plan_approved", "merge");
        probe.AssertExactlyOne("plan_approved", e => e.Fields["agent_id"] == "a1");
    }

    // ---- the RequiresDocker daemon lock (DockerSuiteIsolation.cs) --------------------------------

    /// <summary>
    /// The lock excludes a second holder while it is held, and lets one in the moment it is released.
    ///
    /// <para>Both halves are load-bearing and both are asserted against the SAME path, because the two
    /// ways this can be broken are opposite: a lock that shares (the sharing mode weakened to
    /// <c>FileShare.ReadWrite</c>) admits a concurrent run and isolates nothing, and a lock that never
    /// releases wedges every later run on this machine. The exclusion is provable in-process because
    /// <c>FileShare.None</c> is an <c>flock</c> on the open file description — two opens conflict whether
    /// or not they are in the same process (measured on this box, both ways).</para>
    /// </summary>
    [Fact]
    public void DockerSuiteLock_ExcludesASecondHolder_AndReleasesWhenDisposed()
    {
        var path = NewLockPath();
        try
        {
            var first = DockerSuiteLock.Acquire(path, TimeSpan.FromSeconds(5));
            Assert.Equal(path, first.Path);

            // A concurrent run cannot take it — neither by the raw attempt nor by the waiting form.
            using (var intruder = DockerSuiteLock.TryOpen(path))
            {
                Assert.Null(intruder);
            }

            Assert.Throws<TimeoutException>(
                () => DockerSuiteLock.Acquire(path, TimeSpan.FromMilliseconds(300)).Dispose());

            first.Dispose();

            // …and the next run gets it immediately once the holder is gone.
            using var second = DockerSuiteLock.Acquire(path, TimeSpan.FromSeconds(5));
            Assert.True(second.Waited < TimeSpan.FromSeconds(1),
                $"a released lock should be free, but the next acquire queued {second.Waited}.");
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// A run that finds the daemon busy WAITS for its turn rather than failing. This is the whole
    /// behaviour change — the suites do not stop sharing the egress-proxy singleton, they stop
    /// overlapping — so "it queued and then ran" is the property, not "it errored politely".
    ///
    /// <para>The assertion is on the measured wait, not merely on success: a lock that did not exclude
    /// at all would also return a <see cref="DockerSuiteLock"/> here, and it would return one that had
    /// waited ~0 ms.</para>
    /// </summary>
    [Fact]
    public async Task DockerSuiteLock_QueuesBehindTheCurrentHolder_ThenProceeds()
    {
        var path = NewLockPath();
        var holder = DockerSuiteLock.Acquire(path, TimeSpan.FromSeconds(5));
        try
        {
            var releasing = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                holder.Dispose();
            });

            var waiter = await Task.Run(() => DockerSuiteLock.Acquire(path, TimeSpan.FromSeconds(30)));
            await releasing;

            Assert.True(waiter.Waited >= TimeSpan.FromMilliseconds(500),
                $"the second run should have queued behind the holder; it waited only {waiter.Waited}.");
            waiter.Dispose();
        }
        finally
        {
            holder.Dispose();
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// The startup/teardown sweep's scope: mainguard's own egress topology and nothing else. A predicate
    /// that matched nothing would make the sweep a silent no-op (and leaked segments would keep
    /// exhausting Docker's ~32-deep bridge pool); one that matched too much would delete a neighbouring
    /// project's networks off a shared daemon, which is the failure mode this whole change exists to
    /// stop causing.
    /// </summary>
    [Theory]
    [InlineData("mainguard-agents", true)]                        // the shared default-deny network
    [InlineData("mainguard-egress", true)]                        // the proxy's egress-capable leg
    [InlineData("mainguard-agent-ab12cd34-agent-1", true)]        // MG-36 per-agent segment
    [InlineData("bridge", false)]                                 // docker's own
    [InlineData("mainguard-agents-something", false)]             // near-miss on the segment prefix
    [InlineData("some-other-project__abc__env_default", false)]   // a neighbour on a shared daemon
    public void DockerSuiteSweep_TouchesMainguardNetworksOnly(string networkName, bool expected)
        => Assert.Equal(expected, DockerSuiteFixture.IsMainguardNetwork(networkName));

    /// <summary>
    /// Every RequiresDocker test class is inside the lock, and the two ways a class declares itself a
    /// Docker test agree with each other.
    ///
    /// <para>Membership of <see cref="DockerSuiteCollection"/> is what puts a class inside the daemon
    /// lock; a new Docker suite that carries the trait but forgets the collection runs OUTSIDE it and
    /// silently reintroduces the collisions. That is invisible to every other test, so it is asserted
    /// here by reflection instead of left to reviewer memory. The class-level trait and the
    /// <c>[RequiresDocker*Fact]</c> methods are cross-checked against each other for the same reason: a
    /// class with the attributes but no trait is missing from the CI leg's filter, and a class with the
    /// trait but no such method is a filter entry that runs nothing.</para>
    ///
    /// <para>The floor on the count is deliberate. A reflection query that matched NOTHING would satisfy
    /// every loop below and report green — the exact "instrument that measures nothing" shape this repo
    /// has been bitten by; the floor makes an empty match a failure.</para>
    /// </summary>
    [Fact]
    public void EveryRequiresDockerClass_JoinsTheDockerSuiteCollection()
    {
        var types = typeof(FixtureAcceptanceTests).Assembly.GetTypes();

        var byTrait = types.Where(HasRequiresDockerTrait).Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var byFactAttribute = types.Where(HasRequiresDockerFactMethod).Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(byTrait.Length >= 15,
            $"expected the RequiresDocker suites to be discoverable by trait; found {byTrait.Length}. "
            + "A zero here means this test is measuring nothing.");
        Assert.Equal(byTrait, byFactAttribute);

        foreach (var name in byTrait)
        {
            var type = types.Single(t => t.FullName == name);
            Assert.True(CollectionNameOf(type) == DockerSuiteCollection.Name,
                $"{name} is a RequiresDocker suite but is not in the '{DockerSuiteCollection.Name}' "
                + $"collection (found {CollectionNameOf(type) ?? "no [Collection]"}). Without it the class "
                + "runs outside the daemon lock and can collide with a concurrent run.");
        }
    }

    /// <summary>Does the class carry <c>[Trait("Category","RequiresDocker")]</c>? Read off
    /// <see cref="CustomAttributeData"/> because xunit's <c>TraitAttribute</c> exposes no properties.</summary>
    private static bool HasRequiresDockerTrait(Type type) =>
        type.GetCustomAttributesData().Any(d =>
            d.AttributeType == typeof(TraitAttribute)
            && d.ConstructorArguments.Count == 2
            && (string?)d.ConstructorArguments[0].Value == "Category"
            && (string?)d.ConstructorArguments[1].Value == "RequiresDocker");

    /// <summary>Does the class declare any <c>[RequiresDocker…Fact]</c> test? Matched by attribute name
    /// so all three gates (image, daemon-only, opt-in) count.</summary>
    private static bool HasRequiresDockerFactMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name.StartsWith("RequiresDocker", StringComparison.Ordinal)));

    /// <summary>The class's xunit collection name, or null when it declares none.</summary>
    private static string? CollectionNameOf(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(d => d.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments[0].Value as string;

    private static string NewLockPath() => Path.Combine(
        Path.GetTempPath(), "mainguard-docker-suite-locktest-" + Guid.NewGuid().ToString("N") + ".lock");

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* never fail a test from cleanup */ }
    }

    private static void PushHead(DualRepoFixture dual)
    {
        using var repo = new LibGit2Sharp.Repository(dual.WorkRepoPath);
        var remote = repo.Network.Remotes[DualRepoFixture.QuarantineRemote];
        repo.Network.Push(remote, repo.Head.CanonicalName + ":" + repo.Head.CanonicalName);
    }
}
