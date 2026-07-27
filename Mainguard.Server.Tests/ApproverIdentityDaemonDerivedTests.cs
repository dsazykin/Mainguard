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

        if (OperatingSystem.IsLinux())
        {
            // This test runs in the same process as the in-proc daemon, so "the daemon's euid" is ours.
            Assert.Equal($"uid:{geteuid()}", first);
        }
        else
        {
            Assert.Equal($"os:{Environment.UserName}", first);
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

        // Draft a pending plan directly on the daemon's service (the coordinator's spawn_worker lands here).
        var svc = isolated.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.PlanApprovalService>();
        var fields = new Mainguard.Agents.Agents.Orchestrator.TaskPlanFields(new[] { "src/a.cs" }, "approach", "tests");
        var draft = svc.Draft("coord-1", "Refactor", fields, "prompt", 1.5m);

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
