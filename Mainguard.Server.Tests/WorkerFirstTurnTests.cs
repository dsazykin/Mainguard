using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The daemon half of the first turn: whether a plan-gated worker's jail is started with one at all, and
/// WHERE on the launch line it lands.
///
/// <para><b>The defect.</b> Observed live, on a real claude-code worker. The jail launched
/// <c>claude --append-system-prompt &lt;operating instructions&gt;</c> and nothing else, and the CLI sat
/// interactive with an empty input box — a vendor CLI does not act on a system prompt, it needs a user
/// turn. Six minutes in: outbox empty, no transcript, <c>mainguard-plan</c> never run. And nothing could
/// start it, because the only mechanism that delivers text to a worker's CLI is the coordinator's
/// <c>send_worker_prompt</c>, which is plan-gated: <c>"&lt;id&gt; has not presented a plan yet — no work is
/// authorised."</c> The worker could not present a plan without a first turn, and could not be sent a
/// first turn until it had presented a plan. Phase 2's worker-authored-plan loop could not start once on
/// a real CLI.</para>
///
/// <para><b>Why these tests assert POSITION and not merely presence.</b> Because the obvious fix does not
/// work. Appended last — the position the three neighbouring fields on this launch line use — the turn
/// never reaches the model: <c>--allowedTools</c> is variadic (<c>&lt;tools...&gt;</c>) and swallows every
/// following positional. Measured against claude-code 2.1.250, that spelling idled for the full
/// 90-second probe, indistinguishable from no turn at all; placed first, the same text ran the shim on
/// its first action. A test that asserted only "the turn is on the line somewhere" would have passed
/// against a build that was still deadlocked.</para>
/// </summary>
public sealed class WorkerFirstTurnTests
{
    private static readonly IReadOnlyList<string> Launch = new[] { "/opt/mainguard/adapters/bin/claude" };

    private const string WorkerShim = AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.PlanShimFileName;
    private const string Instructions = "# You are a Mainguard worker";
    private const string IpcDir = "/var/mainguard/agent-ipc/abc";

    /// <summary>
    /// THE TEST THAT WOULD HAVE CAUGHT THE DEADLOCK. A plan-gated worker's jail is launched with a first
    /// user turn. A launch line of nothing but flags is a CLI that will idle forever behind an
    /// input-locked terminal, and that must be a red test rather than a six-minute wait for a transcript
    /// that never appears.
    /// </summary>
    [Fact]
    public void APlanGatedWorkerJail_IsLaunchedWithAFirstUserTurn()
    {
        var argv = Build(ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker)!;

        var turn = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim)!;
        Assert.Contains(turn, argv);
        Assert.Contains(WorkerShim + " brief", turn, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE GUARD THAT MATTERS, and the one the live failure taught. The turn is the FIRST argument after
    /// the CLI's own launch argv, ahead of every flag the daemon appends. Behind
    /// <c>--allowedTools</c> — a variadic option — it is consumed as another tool pattern and never
    /// becomes a turn, which is the same observable behaviour as the bug this change fixes.
    /// </summary>
    [Fact]
    public void TheTurnIsTheFirstArgument_AheadOfEveryFlagTheDaemonAppends()
    {
        var argv = Build(ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker)!;

        Assert.Equal(Launch[0], argv[0]);
        Assert.Equal(AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim), argv[1]);

        // Everything the daemon appends comes after it — proved by position, not by counting.
        var flags = argv.ToList();
        Assert.True(flags.IndexOf("--append-system-prompt") > 1);
        Assert.True(flags.IndexOf("--allowedTools") > 1);
    }

    /// <summary>The rest of the launch line is unchanged: the operating instructions and the single
    /// pre-approval still travel exactly as they did, in the order they did.</summary>
    [Fact]
    public void TheOtherTwoChannelsAreUndisturbed()
    {
        var argv = Build(ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker)!;

        Assert.Equal(
            new[]
            {
                Launch[0],
                AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim)!,
                "--append-system-prompt", Instructions,
                "--allowedTools", "Bash(" + WorkerShim + ":*)",
            },
            argv);
    }

    /// <summary>
    /// A COORDINATOR is launched with no first turn — its jail still gets the instructions and its own
    /// pre-approval, and nothing else. Its terminal is not input-locked, so the human who spawned it
    /// types the request that only they have; the daemon inventing one would set a coordinator fanning
    /// out workers for work nobody asked for.
    /// </summary>
    [Fact]
    public void ACoordinatorJail_IsLaunchedWithNoFirstTurn()
    {
        var argv = Build(ClaudeCode(), IpcDir, AgentIpcEndpointRole.Coordinator)!;

        var coordinatorShim = AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.SpawnShimFileName;
        Assert.Equal(
            new[]
            {
                Launch[0], "--append-system-prompt", Instructions,
                "--allowedTools", "Bash(" + coordinatorShim + ":*)",
            },
            argv);
    }

    /// <summary>
    /// A jail with no IPC dir gets no turn: every external-PR head and every manually spawned worker. It
    /// has no <c>mainguard-plan</c> in it, so a turn telling it to run one would buy a "command not
    /// found" and an agent with no idea what to do next — and none of those sessions is deadlocked,
    /// because the plan gate is not withholding anything from them. Same gate, same reasoning, as the
    /// pre-approval beside it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AJailWithNoIpcDir_GetsNoTurnAndNoGrant(string? ipcDir)
    {
        var argv = Build(ClaudeCode(), ipcDir, AgentIpcEndpointRole.Worker)!;

        Assert.Equal(new[] { Launch[0], "--append-system-prompt", Instructions }, argv);
    }

    /// <summary>An adapter that declares no first-turn channel launches byte-identically to before —
    /// four of the five shipped CLIs, and any CLI a user adds without one.</summary>
    [Fact]
    public void AnAdapterThatDeclaresNoChannel_LaunchesExactlyAsBefore()
    {
        var argv = SandboxAgentLauncher.ApplyInitialPrompt(
            Launch, Marker(null), IpcDir, AgentIpcEndpointRole.Worker);

        Assert.Equal(Launch, argv);
    }

    /// <summary>A marker written before the field existed (the documented "re-install to backfill" state)
    /// degrades to no turn rather than to a stray argument — an unreadable style must never become a bare
    /// positional the CLI reads as something else.</summary>
    [Fact]
    public void AnUnreadableStyleOnAMarker_YieldsNoTurn()
    {
        Assert.Equal(
            Launch,
            SandboxAgentLauncher.ApplyInitialPrompt(
                Launch, Marker("second-positional"), IpcDir, AgentIpcEndpointRole.Worker));
    }

    /// <summary>A CLI with no launch argv at all (an adapter that is a tool, not an agent) is left
    /// alone: there is no process to give a turn to.</summary>
    [Fact]
    public void ALaunchlessAdapter_IsLeftAlone()
    {
        Assert.Null(SandboxAgentLauncher.ApplyInitialPrompt(
            null, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker));
    }

    /// <summary>
    /// THE BOUNDARY, asserted structurally rather than by sampling a string. Phase 2 §2.2's withheld
    /// thing is the TASK, and the first turn must not become a second way out of the gate. Neither the
    /// builder nor the text has a parameter that could carry it: every parameter of
    /// <c>BuildLaunchArgv</c> and <c>ApplyInitialPrompt</c> is the adapter, the shim dir, the role or the
    /// instructions, and <c>AgentKickoffPrompt.For</c> takes a role and a path. Asserting the SHAPE and
    /// not the current contents is deliberate — a test that only checked "today's text does not contain
    /// today's task" would go on passing the day someone threads the task in.
    /// </summary>
    [Fact]
    public void NothingOnTheFirstTurnPath_CanEvenBeHandedTheWithheldTask()
    {
        var allowed = new[]
        {
            typeof(IReadOnlyList<string>), typeof(InstalledAdapterMarker), typeof(string),
            typeof(AgentIpcEndpointRole),

            // The plan-mode toggle (2026-08-30). Admitted to this list because it is an ENUM of two
            // values: it selects which of two compile-time constant turns is sent and is structurally
            // incapable of carrying a task, which is exactly the property this test exists to enforce.
            // A `string planMode` would have been refused here, correctly.
            typeof(Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode),
        };
        Assert.Equal(2, Enum.GetValues<Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode>().Length);

        var methods = new[]
        {
            typeof(SandboxAgentLauncher).GetMethod(
                nameof(SandboxAgentLauncher.BuildLaunchArgv),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!,
            typeof(SandboxAgentLauncher).GetMethod(
                nameof(SandboxAgentLauncher.ApplyInitialPrompt),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!,
            typeof(AgentKickoffPrompt).GetMethod(nameof(AgentKickoffPrompt.For))!,
        };

        Assert.All(methods, m => Assert.All(
            m.GetParameters(), p => Assert.Contains(p.ParameterType, allowed)));

        // And every element of the built line comes from one of those declared inputs — nothing the
        // daemon knows and the worker may not have can appear on it.
        var argv = Build(ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker)!;
        var permitted = new HashSet<string>(StringComparer.Ordinal)
        {
            Launch[0],
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim)!,
            "--append-system-prompt", Instructions,
            "--allowedTools", "Bash(" + WorkerShim + ":*)",
        };
        Assert.All(argv, a => Assert.Contains(a, permitted));

        // The same claim for the OTHER mode: an ungated worker's line carries its own first turn and
        // still nothing else. The turn tells it to ASK for the task; it does not contain one.
        var ungated = SandboxAgentLauncher.BuildLaunchArgv(
            Launch, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker, Instructions,
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Ungated)!;
        var permittedUngated = new HashSet<string>(StringComparer.Ordinal)
        {
            Launch[0],
            AgentKickoffPrompt.For(
                AgentIpcEndpointRole.Worker, WorkerShim,
                Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Ungated)!,
            "--append-system-prompt", Instructions,
            "--allowedTools", "Bash(" + WorkerShim + ":*)",
        };
        Assert.All(ungated, a => Assert.Contains(a, permittedUngated));
    }

    private static IReadOnlyList<string>? Build(
        InstalledAdapterMarker adapter, string? ipcDir, AgentIpcEndpointRole role) =>
        SandboxAgentLauncher.BuildLaunchArgv(Launch, adapter, ipcDir, role, Instructions);

    /// <summary>The marker as the SHIPPED manifest produces it — read from the real file rather than
    /// retyped, so a test cannot keep passing against a declaration the product no longer makes.</summary>
    private static InstalledAdapterMarker ClaudeCode()
    {
        var spec = AdapterManifest.Parse(System.IO.File.ReadAllText(StarterManifestPath()))
            .Adapters.Single(a => a.Id == "claude-code");
        return new InstalledAdapterMarker(
            "claude-code", spec.Version, Launch,
            SystemPromptArg: spec.SystemPromptArg,
            PreApprovedCommandArg: spec.PreApprovedCommandArg,
            PreApprovedCommandFormat: spec.PreApprovedCommandFormat,
            InitialPromptStyle: spec.InitialPromptStyle);
    }

    private static InstalledAdapterMarker Marker(string? style) =>
        new("claude-code", "2.1.218", Launch, InitialPromptStyle: style);

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
