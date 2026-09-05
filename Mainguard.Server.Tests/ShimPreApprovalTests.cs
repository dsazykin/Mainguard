using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The daemon half of the coordinator's pre-approval: WHICH command is granted, to WHICH jail.
///
/// <para><b>The defect.</b> A real claude-code coordinator ran its shim as its first action and hit "This
/// command requires approval" — the jail's <c>/workspace/.claude/settings.local.json</c> pre-approved
/// only what the owner happened to have approved in some earlier session, and nothing pre-approved the
/// one command the role actually has. In a jail nobody is watching, the feature stalled permanently on
/// its opening move.</para>
///
/// <para><b>Why the grant is a launch FLAG and not a merge into that settings file.</b> The settings file
/// is harvested back into the per-repo host store on stop, so a grant written there would have been
/// re-injected into every later jail for that repository — including plain workers and untrusted
/// external-PR heads. A launch flag is per-jail and per-role by construction: written nowhere,
/// harvestable by nothing, gone when the process exits.</para>
///
/// <para>These tests assert what is GRANTED, not that a flag is present. A capability grant inside a
/// least-privilege sandbox is only as good as its narrowness, so the interesting cases are all the ones
/// where nothing should be granted at all.</para>
/// </summary>
public sealed class ShimPreApprovalTests
{
    private static readonly IReadOnlyList<string> Launch = new[] { "/opt/mainguard/adapters/bin/claude" };

    private const string CoordinatorShim = AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.SpawnShimFileName;
    private const string WorkerShim = AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.PlanShimFileName;

    /// <summary>A coordinator's jail pre-approves its spawn shim, by absolute path, in claude-code's own
    /// permission syntax — the exact string the live failure needed and did not have.</summary>
    [Fact]
    public void ACoordinatorJail_PreApprovesItsOwnSpawnShim()
    {
        var argv = Apply(ClaudeCode(), "/var/mainguard/agent-ipc/abc", AgentIpcEndpointRole.Coordinator);

        Assert.Equal(
            new[] { Launch[0], "--allowedTools", "Bash(" + CoordinatorShim + ":*)" },
            argv);
    }

    /// <summary>A plan-gated worker gets ITS shim, and the grant names a different path — the role lock
    /// expressed in the permission rule, not only in which file the daemon wrote.</summary>
    [Fact]
    public void AWorkerJail_PreApprovesItsOwnPlanShim()
    {
        var argv = Apply(ClaudeCode(), "/var/mainguard/agent-ipc/abc", AgentIpcEndpointRole.Worker);

        Assert.Equal(
            new[] { Launch[0], "--allowedTools", "Bash(" + WorkerShim + ":*)" },
            argv);
    }

    /// <summary>
    /// THE GUARD THAT MATTERS. Neither role is ever granted the other's shim, and no jail is granted both.
    /// Verified against the real claude-code CLI as well as here: a prompt asking it to run
    /// <c>mainguard-plan</c> under a <c>mainguard-agent</c> grant answers DENIED, so the rule really is
    /// path-scoped rather than a prefix that happens to cover the directory.
    /// </summary>
    [Theory]
    [InlineData(AgentIpcEndpointRole.Coordinator, WorkerShim)]
    [InlineData(AgentIpcEndpointRole.Worker, CoordinatorShim)]
    public void NoRoleIsEverGrantedTheOtherRolesShim(AgentIpcEndpointRole role, string forbidden)
    {
        var argv = Apply(ClaudeCode(), "/var/mainguard/agent-ipc/abc", role);

        Assert.DoesNotContain(argv!, a => a.Contains(forbidden, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE OTHER GUARD. A jail with no IPC dir has no shim at all — every external-PR head (untrusted
    /// code from outside this machine) and every manually spawned worker the plan gate is not holding.
    /// Deriving the grant from the ROLE STRING alone would have handed those jails a standing
    /// pre-approval for a path that does not exist in them: harmless the day it is written, and exactly
    /// the kind of latent grant that stops being harmless when something else is later mounted there.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AJailWithNoIpcDir_IsGrantedNothing(string? ipcDir)
    {
        var argv = Apply(ClaudeCode(), ipcDir, AgentIpcEndpointRole.Worker);

        Assert.Equal(Launch, argv);
    }

    /// <summary>An adapter that declares no pre-approval channel launches byte-identically to before —
    /// four of the five shipped CLIs, and any CLI a user adds without one.</summary>
    [Fact]
    public void AnAdapterThatDeclaresNoChannel_LaunchesExactlyAsBefore()
    {
        var argv = Apply(Marker(null, null), "/var/mainguard/agent-ipc/abc", AgentIpcEndpointRole.Coordinator);

        Assert.Equal(Launch, argv);
    }

    /// <summary>A marker written before these fields existed (the documented "re-install to backfill"
    /// state) degrades to no grant rather than to a malformed argv — a flag with no value would be handed
    /// to the CLI as its next positional argument.</summary>
    [Fact]
    public void AHalfPopulatedMarker_GrantsNothingRatherThanEmittingAStrayFlag()
    {
        Assert.Equal(Launch, Apply(Marker("--allowedTools", null), "/ipc", AgentIpcEndpointRole.Coordinator));
        Assert.Equal(Launch, Apply(Marker(null, "Bash({command}:*)"), "/ipc", AgentIpcEndpointRole.Coordinator));
    }

    /// <summary>A CLI with no launch argv at all (an adapter that is a tool, not an agent) is left alone:
    /// there is no process to grant anything to.</summary>
    [Fact]
    public void ALaunchlessAdapter_IsLeftAlone()
    {
        Assert.Null(SandboxAgentLauncher.ApplyShimPreApproval(
            null, ClaudeCode(), "/ipc", AgentIpcEndpointRole.Coordinator));
    }

    /// <summary>
    /// The grant is exactly one argv pair, appended. Nothing already on the launch line — the operating
    /// instructions above all, which arrive as <c>--append-system-prompt &lt;several KiB&gt;</c> — is
    /// disturbed or duplicated.
    /// </summary>
    [Fact]
    public void TheGrantIsAppended_AndDisturbsNothingAlreadyOnTheLaunchLine()
    {
        var existing = new[] { Launch[0], "--append-system-prompt", "# You are the Mainguard Coordinator" };

        var argv = SandboxAgentLauncher.ApplyShimPreApproval(
            existing, ClaudeCode(), "/ipc", AgentIpcEndpointRole.Coordinator);

        Assert.Equal(existing, argv!.Take(existing.Length));
        Assert.Equal(existing.Length + 2, argv.Count);
    }

    private static IReadOnlyList<string>? Apply(
        InstalledAdapterMarker adapter, string? ipcDir, AgentIpcEndpointRole role) =>
        SandboxAgentLauncher.ApplyShimPreApproval(Launch, adapter, ipcDir, role);

    /// <summary>The marker as the SHIPPED manifest produces it — read from the real file rather than
    /// retyped, so a test cannot keep passing against a declaration the product no longer makes.</summary>
    private static InstalledAdapterMarker ClaudeCode()
    {
        var spec = AdapterManifest.Parse(System.IO.File.ReadAllText(StarterManifestPath()))
            .Adapters.Single(a => a.Id == "claude-code");
        return Marker(spec.PreApprovedCommandArg, spec.PreApprovedCommandFormat);
    }

    private static InstalledAdapterMarker Marker(string? arg, string? format) =>
        new("claude-code", "2.1.218", Launch,
            PreApprovedCommandArg: arg, PreApprovedCommandFormat: format);

    private static string StarterManifestPath()
    {
        for (var probe = new System.IO.DirectoryInfo(AppContext.BaseDirectory); probe is not null; probe = probe.Parent)
        {
            var candidate = System.IO.Path.Combine(
                probe.FullName, "Mainguard.Agents", "Agents", "Adapters", "adapters.starter.json");
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("adapters.starter.json not found above " + AppContext.BaseDirectory);
    }
}
