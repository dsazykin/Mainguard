using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The operator's model choice reaching the CLI's actual launch line.
///
/// <para>This is the half that decides whether the setting is real or decorative. The model has to be an
/// argument on the process the daemon starts — there is no other channel — so a setting that is stored,
/// rendered and never appended is a picker that changes nothing, which is the failure this file exists
/// to prevent.</para>
/// </summary>
public class AgentModelLaunchArgvTests
{
    private static readonly IReadOnlyList<string> Launch = new[] { "/opt/mainguard/adapters/bin/claude" };
    private const string Instructions = "# You are a Mainguard worker";
    private const string IpcDir = "/var/mainguard/agent-ipc/abc";

    [Fact]
    public void AChosenModel_IsAppendedAsTheAdaptersDeclaredFlag()
    {
        var argv = SandboxAgentLauncher.BuildLaunchArgv(
            Launch, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker, Instructions,
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated, model: "opus")!;

        // Adjacency is the assertion, not mere presence: a flag and its value separated by anything is a
        // different command line.
        var flag = argv.ToList().IndexOf("--model");
        Assert.True(flag >= 0, "the launch line carries no --model at all: " + string.Join(" ", argv));
        Assert.Equal("opus", argv[flag + 1]);
    }

    [Fact]
    public void NoChoice_LeavesTheLaunchLineExactlyAsItWas()
    {
        var withoutModel = SandboxAgentLauncher.BuildLaunchArgv(
            Launch, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker, Instructions)!;

        // Unset must be byte-identical to the pre-feature line — an agent whose operator chose nothing
        // keeps its CLI's own default, and Mainguard adds no opinion of its own.
        Assert.DoesNotContain("--model", withoutModel);
    }

    [Fact]
    public void ABlankModel_IsTreatedAsNoChoice()
    {
        foreach (var blank in new[] { "", "   ", null })
        {
            var argv = SandboxAgentLauncher.BuildLaunchArgv(
                Launch, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker, Instructions,
                Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated, model: blank)!;

            Assert.DoesNotContain("--model", argv);
        }
    }

    /// <summary>
    /// A CLI that declares no model flag gets nothing on its line even when a model is supplied. The
    /// alternative — inventing <c>--model</c> for it — is the guess that turns a vendor's argument
    /// parser into a failed spawn for every agent of that kind.
    /// </summary>
    [Fact]
    public void AnAdapterWithNoDeclaredFlag_GetsNothing_EvenWithAModelSupplied()
    {
        var undeclared = new InstalledAdapterMarker("mystery-cli", "1.0.0", Launch);

        var argv = SandboxAgentLauncher.BuildLaunchArgv(
            Launch, undeclared, IpcDir, AgentIpcEndpointRole.Worker, Instructions,
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated, model: "opus")!;

        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("opus", argv);
    }

    /// <summary>The model is a launch argument and nothing more — it must not disturb the first turn,
    /// the instructions or the one pre-approved command, whose ORDER is load-bearing.</summary>
    [Fact]
    public void TheModel_DoesNotDisturbTheRestOfTheLine()
    {
        var withModel = SandboxAgentLauncher.BuildLaunchArgv(
            Launch, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker, Instructions,
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated, model: "opus")!;
        var withoutModel = SandboxAgentLauncher.BuildLaunchArgv(
            Launch, ClaudeCode(), IpcDir, AgentIpcEndpointRole.Worker, Instructions)!;

        // Everything the line had before is still there, in the same order; the model is purely additive.
        Assert.Equal(withoutModel, withModel.Where(a => a != "--model" && a != "opus").ToList());
    }

    /// <summary>The marker as the SHIPPED manifest produces it — read from the real file rather than
    /// retyped, so this cannot keep passing against a declaration the product no longer makes.</summary>
    private static InstalledAdapterMarker ClaudeCode()
    {
        var spec = AdapterManifest.Parse(System.IO.File.ReadAllText(StarterManifestPath()))
            .Adapters.Single(a => a.Id == "claude-code");
        return new InstalledAdapterMarker(
            "claude-code", spec.Version, Launch,
            SystemPromptArg: spec.SystemPromptArg,
            PreApprovedCommandArg: spec.PreApprovedCommandArg,
            PreApprovedCommandFormat: spec.PreApprovedCommandFormat,
            InitialPromptStyle: spec.InitialPromptStyle,
            ModelArg: spec.ModelArg,
            Models: spec.Models);
    }

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
