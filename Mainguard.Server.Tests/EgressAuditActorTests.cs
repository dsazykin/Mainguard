using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-40 (SA-1/F2, same rule as plan approval): the actor on an <c>allowlist_changed</c> audit event is
/// derived daemon-side from the authenticated connection, never taken from the request. A self-asserted
/// actor makes the egress change log worthless as evidence — token-holding host malware could widen the
/// default-deny allowlist and sign the line "operator". Two proofs: (a) neither mutation request carries an
/// actor field, so a hand-crafted client cannot supply one; (b) the recorded actor equals the daemon's
/// identity-resolver value.
/// </summary>
public class EgressAuditActorTests
{
    private sealed class FakeIdentityResolver : IApproverIdentityResolver
    {
        private readonly string _identity;
        public FakeIdentityResolver(string identity) => _identity = identity;
        public string Resolve(ServerCallContext context) => _identity;
    }

    /// <summary>The real substrate facade with only the egress policy swapped for an in-memory allowlist
    /// over the host's audit log: the assertion is about the RECORDED ACTOR, and re-rendering a proxy
    /// container that does not exist on a test box costs a minute of timeouts per call.</summary>
    private sealed class EgressOverrideEnvironment : IAgentEnvironment
    {
        private readonly IAgentEnvironment _inner;

        public EgressOverrideEnvironment(IAgentEnvironment inner, IAuditLog audit)
        {
            _inner = inner;
            Egress = new StubEgress(audit);
        }

        public string SubstrateId => _inner.SubstrateId;

        public SubstrateCapabilities Capabilities => _inner.Capabilities;

        public IRepoProvisioner Repos => _inner.Repos;

        public IAgentWorktreeManager Worktrees => _inner.Worktrees;

        public ISandboxEngine Sandboxes => _inner.Sandboxes;

        public IEgressPolicy Egress { get; }

        public SyncRemote ResolveSyncRemote(string repoHash) => _inner.ResolveSyncRemote(repoHash);

        private sealed class StubEgress : IEgressPolicy
        {
            public StubEgress(IAuditLog audit) => Allowlist = EgressAllowlist.WithDefaults(audit);

            public EgressAllowlist Allowlist { get; }

            public string NetworkName => "fake-net";

            public string ProxyUrl => "http://fake-proxy:3128";

            public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

            public EgressVerdict Evaluate(string host) => Allowlist.Allows(host) ? EgressVerdict.Allowed : EgressVerdict.Denied;
        }
    }

    [Fact]
    public void AllowlistMutations_CarryNoClientActorField()
    {
        // The `who` field numbers are reserved in egress.proto — there is nothing left for a client to set.
        var addFields = AddAllowlistHostRequest.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name).ToArray();
        var removeFields = RemoveAllowlistHostRequest.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name).ToArray();

        Assert.Contains("host_pattern", addFields);
        Assert.DoesNotContain(addFields, n => n is "who" or "actor" or "operator" || n.Contains("identit"));
        Assert.DoesNotContain(removeFields, n => n is "who" or "actor" or "operator" || n.Contains("identit"));
    }

    [Fact]
    public async Task AddAndRemoveAllowlistHost_RecordDaemonDerivedActor()
    {
        using var fixture = new DaemonFixture();
        using var isolated = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            // Replace the peer-credential resolver with a deterministic daemon-side value.
            services.AddSingleton<IApproverIdentityResolver>(new FakeIdentityResolver("peer-uid-4242"));
            services.AddSingleton<IAgentEnvironment>(sp =>
            {
                var audit = sp.GetRequiredService<IAuditLog>();
                return new EgressOverrideEnvironment(new Wsl2AgentEnvironment(auditLog: audit), audit);
            });
        }));

        var token = isolated.Services.GetRequiredService<SessionTokenFile>().Token;
        var headers = new Metadata { { "authorization", $"bearer {token}" } };
        var client = new EgressService.EgressServiceClient(
            GrpcChannel.ForAddress(isolated.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = isolated.Server.CreateHandler() }));

        await client.AddAllowlistHostAsync(
            new AddAllowlistHostRequest { Name = "Example", HostPattern = "example.test", Kind = "Custom" }, headers);
        await client.RemoveAllowlistHostAsync(
            new RemoveAllowlistHostRequest { HostPattern = "example.test" }, headers);

        var changes = isolated.Services.GetRequiredService<IAuditLog>().Read()
            .Where(e => e.Type == EgressAllowlist.ChangeEventType)
            .Where(e => e.Fields.TryGetValue("entry", out var entry) && entry == "example.test")
            .ToList();

        Assert.Equal(2, changes.Count); // one add, one remove
        Assert.All(changes, e => Assert.Equal("peer-uid-4242", e.Fields["who"]));
        Assert.Equal(new[] { "add", "remove" }, changes.Select(e => e.Fields["action"]).ToArray());
    }
}
