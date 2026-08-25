using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-14 test 11 (OPS SA-1/F2, PR-blocking) — the recorded plan approver is daemon-derived from the
/// authenticated connection, NEVER a client-supplied field. Two proofs: (a) the <c>ApprovePlanRequest</c>
/// proto has no identity/approver field at all, so a hand-crafted client cannot supply one; (b) the
/// recorded + echoed identity equals the daemon's peer-credential resolver value, independent of the
/// request.
/// </summary>
public class ApproverIdentityDaemonDerivedTests
{
    private sealed class FakeIdentityResolver : IApproverIdentityResolver
    {
        private readonly string _identity;
        public FakeIdentityResolver(string identity) => _identity = identity;
        public string Resolve(ServerCallContext context) => _identity;
    }

    /// <summary>
    /// MG-16 — what the shipped resolver ACTUALLY returns, asserted rather than described. Loopback TCP
    /// carries no peer credential (<c>SO_PEERCRED</c> is a Unix-domain-socket facility), so
    /// <see cref="PeerCredentialIdentityResolver"/> reports the DAEMON's own OS identity: a constant, the
    /// same for every caller. Locking that down keeps the code and its documentation honest — an approval
    /// record attributes the host session, and cannot say which local principal approved. Changing that
    /// is a transport/trust-model decision, not a refactor.
    ///
    /// <para>W5 — the shape is now <c>os:&lt;name&gt;</c> on EVERY platform, Linux included. The old
    /// Linux-only <c>uid:&lt;euid&gt;</c> branch was a leftover of the retracted <c>SO_PEERCRED</c>
    /// framing (a peer credential is a number), and it made the same daemon-session attribution render
    /// as a bare <c>uid:1000</c> on Windows/WSL2 — where the daemon runs in-VM as <c>User=mainguard</c> —
    /// against <c>os:&lt;name&gt;</c> on a macOS host. <c>uid:</c> survives only as the last resort for a
    /// euid with no passwd entry, where <c>Environment.UserName</c> returns "" and a bare <c>"os:"</c>
    /// would be a blank actor. This asserts the normal path AND pins the regression: on Linux the value
    /// is specifically NOT the raw euid form any more.</para>
    /// </summary>
    [Fact]
    public void PeerCredentialResolver_ReportsTheDaemonsOwnIdentity_ConstantForEveryCaller()
    {
        var resolver = new PeerCredentialIdentityResolver();

        // The call context is never consulted — there is nothing on a loopback TCP connection to read —
        // so resolution succeeds even with no context at all, and cannot vary between callers.
        var first = resolver.Resolve(null!);
        var second = resolver.Resolve(null!);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);

        // Any real test box (CI included) has a passwd entry for its own euid, so the friendly name is
        // the path under test; the uid fallback is unreachable here by construction.
        Assert.False(string.IsNullOrWhiteSpace(Environment.UserName));

        // This test runs in the same process as the in-proc daemon, so "the daemon's user" is ours.
        Assert.Equal($"os:{Environment.UserName}", first);

        if (OperatingSystem.IsLinux())
        {
            // Regression pin: Linux no longer takes a separate raw-euid branch.
            Assert.DoesNotContain("uid:", first);
            Assert.NotEqual($"uid:{geteuid()}", first);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [Fact]
    public void ApproverIdentity_IsDaemonDerived_NotClientField_ProtoHasNoIdentityField()
    {
        // The approval request carries ONLY plan_id — there is no client identity/approver/os field to set.
        var fieldNames = ApprovePlanRequest.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name).ToArray();

        Assert.Contains("plan_id", fieldNames);
        Assert.DoesNotContain(fieldNames, n =>
            n.Contains("identit") || n.Contains("approv") || n.Contains("os_") || n == "os" || n.Contains("uid"));
    }

    [Fact]
    public async Task ApproverIdentity_IsDaemonDerived_NotClientField_RecordsResolverValue()
    {
        using var fixture = new DaemonFixture();
        using var isolated = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            // Replace the peer-credential resolver with a deterministic daemon-side value.
            services.AddSingleton<IApproverIdentityResolver>(new FakeIdentityResolver("peer-uid-1000"));
        }));

        // Present a plan directly on the daemon's service (where a worker's plan shim lands it). The
        // worker id is unique per run: the whole assembly shares one test data root (and therefore one
        // restart-safe plan store), so a literal id shared with another test trips the daemon's
        // one-live-plan-per-worker invariant.
        var svc = isolated.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.PlanApprovalService>();
        var fields = new Mainguard.Agents.Agents.Orchestrator.TaskPlanFields(new[] { "src/a.cs" }, "approach", "tests");
        var draft = svc.Present("worker-" + Guid.NewGuid().ToString("N")[..8], "coord-1", "Refactor", fields, "prompt", 1.5m);
        Assert.True(draft.IsPresented, draft.Message);

        var token = isolated.Services.GetRequiredService<SessionTokenFile>().Token;
        var headers = new Metadata { { "authorization", $"bearer {token}" } };
        var client = new PlanApprovalService.PlanApprovalServiceClient(
            GrpcChannel.ForAddress(isolated.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = isolated.Server.CreateHandler() }));

        // Approve — the client sends only the plan id; it cannot influence the approver.
        var response = await client.ApprovePlanAsync(new ApprovePlanRequest { PlanId = draft.PlanId }, headers);

        Assert.True(response.Approved);
        Assert.Equal("peer-uid-1000", response.ApproverIdentity); // daemon-derived, echoed back

        // The persisted approval record carries the daemon-derived identity.
        var persisted = svc.Get(draft.PlanId!);
        Assert.NotNull(persisted);
        Assert.Equal("peer-uid-1000", persisted!.ApproverIdentity);
    }
}
