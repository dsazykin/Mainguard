using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F11 / F43 — the coordinator role is an ALLOWLIST, and the RPCs the old deny list left reachable are
/// refused.
///
/// <para>The deny list was correct about every entry it had and wrong about its shape: it named ~30
/// dangerous methods out of ~50 and left the rest allowed by default, including <c>SpawnAgent</c> (a
/// manual agent with a full worktree — the role lock undone by one RPC), <c>StopAgent</c>,
/// <c>TerminalService/Attach</c> (the live form of the read <c>GetScrollback</c> was explicitly denied
/// for), <c>HarvestAgentCredentials</c>, the kill switch, the verification/diff/queue reads of F43, egress
/// policy writes, <c>SetBudgets</c> and the repo-provisioning mutations. Three auditors found different
/// subsets of that residue independently — the signature of a control that fails open quietly.</para>
///
/// <para>Latent today, and that is the point: nothing mints a coordinator token in production, so this is
/// a control that would have started mattering exactly when somebody wired one up.</para>
/// </summary>
public sealed class CoordinatorAllowlistTests
{
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    /// <summary>The role gate's own message — asserting it is what distinguishes a role denial from
    /// <c>BearerTokenInterceptor</c>'s "Invalid bearer token", which is also PermissionDenied.</summary>
    private const string RoleDenialMarker = "coordinator role";

    [Fact]
    public async Task Coordinator_IsDenied_SpawnAgent_AndStopAgent()
    {
        using var fixture = Coordinator(out var headers);
        var agents = new AgentService.AgentServiceClient(fixture.CreateChannel());

        await AssertRoleDeniedAsync(() => agents.SpawnAgentAsync(
            new SpawnAgentRequest { RepoHandle = "repo", AgentKind = "claude-code" }, headers).ResponseAsync);
        await AssertRoleDeniedAsync(() => agents.StopAgentAsync(
            new StopAgentRequest { AgentId = "victim" }, headers).ResponseAsync);
    }

    [Fact]
    public async Task Coordinator_IsDenied_HarvestAgentCredentials()
    {
        using var fixture = Coordinator(out var headers);
        var agents = new AgentService.AgentServiceClient(fixture.CreateChannel());

        await AssertRoleDeniedAsync(() => agents.HarvestAgentCredentialsAsync(
            new HarvestAgentCredentialsRequest { AgentId = "victim" }, headers).ResponseAsync);
    }

    [Fact]
    public async Task Coordinator_IsDenied_TerminalAttach()
    {
        using var fixture = Coordinator(out var headers);
        var terminal = new TerminalService.TerminalServiceClient(fixture.CreateChannel());

        using var call = terminal.Attach(headers);
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "victim" });
            while (await call.ResponseStream.MoveNext(System.Threading.CancellationToken.None)) { }
        });

        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.Contains(RoleDenialMarker, error.Status.Detail);
    }

    [Fact]
    public async Task Coordinator_IsDenied_TheKillSwitch()
    {
        using var fixture = Coordinator(out var headers);
        var kill = new KillSwitchService.KillSwitchServiceClient(fixture.CreateChannel());

        await AssertRoleDeniedAsync(() => kill.EngageAsync(new EngageKillRequest(), headers).ResponseAsync);
        await AssertRoleDeniedAsync(() => kill.ResumeAsync(new ResumeKillRequest(), headers).ResponseAsync);
    }

    /// <summary>F43: verification, its log, the merge diff, and the whole-fleet queue stream.</summary>
    [Fact]
    public async Task Coordinator_IsDenied_VerificationAndQueueReads()
    {
        using var fixture = Coordinator(out var headers);
        var queue = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());

        await AssertRoleDeniedAsync(() => queue.RunVerificationAsync(
            new RunVerificationRequest { RepoHandle = "repo", AgentId = "a" }, headers).ResponseAsync);
        await AssertRoleDeniedAsync(() => queue.GetVerificationLogAsync(
            new GetVerificationLogRequest { RepoHandle = "repo", AgentId = "a" }, headers).ResponseAsync);
        await AssertRoleDeniedAsync(() => queue.GetMergeDiffAsync(
            new GetMergeDiffRequest { RepoHandle = "repo", AgentId = "a" }, headers).ResponseAsync);

        using var stream = queue.StreamQueue(new StreamQueueRequest { RepoHandle = "repo" }, headers);
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await stream.ResponseStream.MoveNext(System.Threading.CancellationToken.None)) { }
        });
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.Contains(RoleDenialMarker, error.Status.Detail);
    }

    /// <summary>
    /// The default-deny property itself, rather than one more example of it. Every entry the annotated
    /// record still carries must be absent from the allowlist — so the record cannot decay into a
    /// comment describing a control that no longer exists — and, more importantly, so must every RPC that
    /// is on neither list, because that is the class the old shape allowed by accident.
    /// </summary>
    [Fact]
    public void EveryRecordedDenial_IsAbsentFromTheAllowlist()
    {
        var allowed = AllowedMethods();
        foreach (var denied in RoleInterceptor.CoordinatorDeniedMethods)
        {
            Assert.DoesNotContain(denied, allowed);
        }
    }

    /// <summary>
    /// The reason the allowlist is worth its cost: this enumerates EVERY mapped RPC from the generated
    /// service descriptors and asserts each is either explicitly allowed or (by construction) denied.
    /// It cannot fail today — absence is denial — which is exactly the point: it is here to name, in one
    /// place, the full surface a coordinator credential can reach, so a future addition to the allowlist
    /// is a visible diff on a short list rather than an invisible default.
    /// </summary>
    [Fact]
    public void TheCoordinatorSurface_IsSmallAndEnumerated()
    {
        var allowed = AllowedMethods();
        var all = AllMappedMethods();

        Assert.NotEmpty(all);
        foreach (var method in allowed)
        {
            Assert.Contains(method, all);
        }

        // Reads plus its own conversation. If this number grows, the diff that grew it is the review.
        Assert.Equal(15, allowed.Count);
        Assert.True(
            allowed.Count * 3 < all.Count,
            $"the coordinator can reach {allowed.Count} of {all.Count} RPCs; a surface approaching the "
            + "whole API is a deny list wearing an allowlist's name.");
    }

    private static HashSet<string> AllowedMethods()
    {
        var field = typeof(RoleInterceptor).GetField(
            "CoordinatorAllowedMethods", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(null)!;
    }

    /// <summary>Every RPC on every generated service, read from the protobuf service descriptors.</summary>
    private static HashSet<string> AllMappedMethods()
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in typeof(AgentService).Assembly
                     .GetTypes()
                     .Where(t => t.Namespace == "Mainguard.Protos.V1" && t.IsClass && t.IsAbstract && t.IsSealed)
                     .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                     .Where(p => p.PropertyType == typeof(Google.Protobuf.Reflection.FileDescriptor))
                     .Select(p => (Google.Protobuf.Reflection.FileDescriptor)p.GetValue(null)!)
                     .SelectMany(f => f.Services))
        {
            foreach (var method in descriptor.Methods)
            {
                methods.Add($"/{descriptor.FullName}/{method.Name}");
            }
        }

        return methods;
    }

    private static DaemonFixture Coordinator(out Metadata headers)
    {
        var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);
        headers = fixture.AuthHeaders(CoordinatorToken);
        return fixture;
    }

    private static async Task AssertRoleDeniedAsync(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.Contains(RoleDenialMarker, error.Status.Detail);
        Assert.DoesNotContain("Invalid bearer token", error.Status.Detail);
    }
}
