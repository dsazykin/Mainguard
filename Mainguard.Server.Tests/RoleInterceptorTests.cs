using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-14 test 6 (TI-P2-14.7) — the coordinator role is interceptor-enforced, not convention. A connection
/// authenticated with a coordinator credential is denied the merge RPCs, the human-only plan-approval
/// RPCs, and (MG-30) the scrollback read, with <see cref="StatusCode.PermissionDenied"/>. The operator
/// credential is not denied.
///
/// <para><b>MG-12.</b> These previously proved nothing. <c>BearerTokenInterceptor</c> accepted only the
/// operator token and runs BEFORE <c>RoleInterceptor</c>, so a coordinator token died there with its own
/// <c>PermissionDenied</c> ("Invalid bearer token") and the role gate was never reached — the assertions
/// passed for the wrong reason, and the role layer was unreachable dead code in production
/// (<c>IssueCoordinatorToken</c> had no callers either). Now the coordinator token genuinely
/// authenticates, so every case below asserts on the <b>role gate's own message</b>, not just the status
/// code — an assertion the old wiring could not have satisfied.</para>
/// </summary>
public class RoleInterceptorTests
{
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    /// <summary>The role gate's message. Asserting it is what distinguishes a role denial from the
    /// bearer layer's "Invalid bearer token" denial — both are PermissionDenied.</summary>
    private const string RoleDenialMarker = "coordinator role";

    [Fact]
    public async Task CoordinatorToken_Authenticates_ThenIsDeniedByTheRoleLayer_NotTheBearerLayer()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.BeginMergeAsync(
                new BeginMergeRequest { RepoHandle = "repo", AgentId = "a" },
                fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        // The ROLE gate rejected it — not BearerTokenInterceptor.
        Assert.Contains(RoleDenialMarker, ex.Status.Detail);
        Assert.DoesNotContain("Invalid bearer token", ex.Status.Detail);
    }

    [Fact]
    public async Task RoleInterceptor_DeniesMergeToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());
        var coordinatorHeaders = fixture.AuthHeaders(CoordinatorToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.BeginMergeAsync(new BeginMergeRequest { RepoHandle = "repo", AgentId = "a" }, coordinatorHeaders).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(RoleDenialMarker, ex.Status.Detail);

        var ex2 = await Assert.ThrowsAsync<RpcException>(() =>
            merge.ConfirmMergeAsync(new ConfirmMergeRequest { RepoHandle = "repo", AgentId = "a" }, coordinatorHeaders).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex2.StatusCode);
        Assert.Contains(RoleDenialMarker, ex2.Status.Detail);
    }

    [Fact]
    public async Task RoleInterceptor_DeniesRejectToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());

        // Rejecting is the review verdict "no" — merge power's other terminal; an agent that could
        // reject a co-tenant's verified branch would hold veto over work it competes with.
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.RejectEntryAsync(
                new RejectEntryRequest { RepoHandle = "repo", AgentId = "a" },
                fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(RoleDenialMarker, ex.Status.Detail);
    }

    [Fact]
    public async Task RoleInterceptor_DeniesPauseAndUnpauseToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var agents = new AgentService.AgentServiceClient(fixture.CreateChannel());
        var headers = fixture.AuthHeaders(CoordinatorToken);

        // An agent that could freeze a co-tenant's jail could rig the queue; one that could unpause
        // could break the cascade's critical section open mid-write.
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            agents.PauseAgentAsync(new PauseAgentRequest { AgentId = "a" }, headers).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(RoleDenialMarker, ex.Status.Detail);

        var ex2 = await Assert.ThrowsAsync<RpcException>(() =>
            agents.UnpauseAgentAsync(new UnpauseAgentRequest { AgentId = "a" }, headers).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex2.StatusCode);
        Assert.Contains(RoleDenialMarker, ex2.Status.Detail);
    }

    [Fact]
    public async Task RoleInterceptor_DeniesPlanApprovalToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var plans = new PlanApprovalService.PlanApprovalServiceClient(fixture.CreateChannel());

        // The coordinator cannot approve its OWN plans (self-approval is the threat).
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            plans.ApprovePlanAsync(new ApprovePlanRequest { PlanId = "any" }, fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(RoleDenialMarker, ex.Status.Detail);
    }

    // MG-30: the scrollback ring serves up to 1000 rows of another agent's session per page.
    [Fact]
    public async Task RoleInterceptor_DeniesScrollbackToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var terminal = new TerminalService.TerminalServiceClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            terminal.GetScrollbackAsync(
                new ScrollbackRequest { AgentId = "someone-elses-agent", Start = 0, Count = 1000 },
                fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(RoleDenialMarker, ex.Status.Detail);
    }

    // The operator drives the UI and legitimately reads any agent's scrollback.
    [Fact]
    public async Task Operator_MayReadScrollback()
    {
        using var fixture = new DaemonFixture();

        var terminal = new TerminalService.TerminalServiceClient(fixture.CreateChannel());

        // No bound session for the agent → an empty reply, NOT PermissionDenied.
        var reply = await terminal.GetScrollbackAsync(
            new ScrollbackRequest { AgentId = "no-such-agent", Start = 0, Count = 10 },
            fixture.AuthHeaders());

        Assert.Empty(reply.Rows);
    }

    [Fact]
    public async Task RoleInterceptor_OperatorNotDenied_MergeRpcPasses()
    {
        using var fixture = new DaemonFixture();

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());

        // The operator credential is not a coordinator — the role check passes. (No active queue for the
        // handle → NotFound, which proves the call was NOT blocked by PermissionDenied.)
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.BeginMergeAsync(new BeginMergeRequest { RepoHandle = "no-such-repo", AgentId = "a" }, fixture.AuthHeaders()).ResponseAsync);
        Assert.NotEqual(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    // Accepting coordinator credentials must NOT have widened authentication to "any token".
    [Fact]
    public async Task UnknownToken_IsStillRejectedByTheBearerLayer()
    {
        using var fixture = new DaemonFixture();

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.BeginMergeAsync(
                new BeginMergeRequest { RepoHandle = "repo", AgentId = "a" },
                fixture.AuthHeaders("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains("Invalid bearer token", ex.Status.Detail);
    }
}
