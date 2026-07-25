using System;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="EgressService"/> — the App's only path to the daemon-owned default-deny
/// egress allowlist (P2-07 / ESC-I2). <c>List</c> reads the live entries; <c>Add</c>/<c>Remove</c> mutate
/// the allowlist (change-logged by <see cref="EgressAllowlist"/>) and re-render the running proxy so the
/// change takes effect immediately. Transport only — the policy lives behind <see cref="IEgressPolicy"/>.
/// This is what makes the block-notification prompt's "unblock" affordance (Fix 2) actually take effect,
/// and it wires the App's egress editor to the daemon (replacing the in-memory stand-in).
///
/// <para><b>SA-1/F2 (binding):</b> the actor recorded on every <c>allowlist_changed</c> event is resolved
/// <b>daemon-side</b> from the authenticated connection via <see cref="IApproverIdentityResolver"/> — the
/// same resolver plan approval uses, and the requests carry no actor field (the proto reserves it). A
/// client-supplied <c>who</c> is self-asserted: token-holding host malware could widen the default-deny
/// egress and sign the change with any name it liked, which makes the change log unusable as evidence.</para>
/// </summary>
public sealed class EgressGrpcService : EgressService.EgressServiceBase
{
    private readonly IEgressPolicy _egress;
    private readonly IApproverIdentityResolver _identity;

    public EgressGrpcService(IAgentEnvironment environment, IApproverIdentityResolver identity)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _egress = environment.Egress;
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public override Task<ListAllowlistResponse> ListAllowlist(ListAllowlistRequest request, ServerCallContext context)
    {
        var response = new ListAllowlistResponse();
        foreach (var e in _egress.Allowlist.Entries)
        {
            response.Entries.Add(new Protos.V1.AllowlistEntry
            {
                Name = e.Name,
                HostPattern = e.HostPattern,
                Kind = e.Kind.ToString(),
                DefeatsA6 = e.DefeatsA6,
            });
        }

        return Task.FromResult(response);
    }

    public override async Task<AddAllowlistHostResponse> AddAllowlistHost(
        AddAllowlistHostRequest request, ServerCallContext context)
    {
        var host = request.HostPattern?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A host pattern is required."));
        }

        var kind = Enum.TryParse<EgressEntryKind>(request.Kind, ignoreCase: true, out var k) ? k : EgressEntryKind.Custom;
        var name = string.IsNullOrWhiteSpace(request.Name) ? host : request.Name.Trim();
        // The audit actor comes from the connection, never from the message (SA-1/F2).
        var who = _identity.Resolve(context);

        var alreadyAllowed = _egress.Allowlist.Allows(host);
        var entry = new EgressAllowlistEntry(name, host, kind);
        _egress.Allowlist.Add(entry, who); // a duplicate host is a no-op, still audited

        // Re-render the running proxy so the host is reachable NOW; best-effort — the next spawn's
        // EnsureReadyAsync re-renders regardless, so an add still succeeds when the proxy isn't up yet.
        await TryReRenderAsync(context.CancellationToken).ConfigureAwait(false);

        return new AddAllowlistHostResponse { Added = !alreadyAllowed, DefeatsA6 = entry.DefeatsA6 };
    }

    public override async Task<RemoveAllowlistHostResponse> RemoveAllowlistHost(
        RemoveAllowlistHostRequest request, ServerCallContext context)
    {
        var host = request.HostPattern?.Trim() ?? string.Empty;
        var wasAllowed = _egress.Allowlist.Allows(host);
        _egress.Allowlist.Remove(host, _identity.Resolve(context)); // daemon-derived actor (SA-1/F2)
        await TryReRenderAsync(context.CancellationToken).ConfigureAwait(false);
        return new RemoveAllowlistHostResponse { Removed = wasAllowed };
    }

    private async Task TryReRenderAsync(System.Threading.CancellationToken ct)
    {
        try { await _egress.EnsureReadyAsync(ct).ConfigureAwait(false); }
        catch (Exception) { /* proxy not up yet — the change lands on the next spawn's EnsureReadyAsync */ }
    }
}
