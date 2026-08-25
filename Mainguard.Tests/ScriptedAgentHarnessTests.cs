using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Mainguard.Tests.TestTools.ScriptedAgent;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-00 §A.4.2 acceptance — the ScriptedAgentHarness self-test: a yield round-trip
/// over a bare stdin/stdout pipe. The harness emits <c>[IPC_UPDATE_REQUESTED]</c> and
/// blocks until it reads <c>[IPC_UPDATE_READY]</c>, then continues (the P2-09 control
/// protocol every later orchestration test drives).
/// </summary>
public sealed class ScriptedAgentHarnessTests
{
    [Fact]
    public async Task Harness_YieldRoundTrip_OverBarePipe()
    {
        using var process = StartHarness("emit:started;yield;emit:resumed;exit:0");

        Assert.Equal("started", await process.StandardOutput.ReadLineAsync());

        // The harness has reached a yield and is waiting for the ready token.
        Assert.Equal(HarnessEntry.UpdateRequested, await process.StandardOutput.ReadLineAsync());

        await process.StandardInput.WriteLineAsync(HarnessEntry.UpdateReady);
        await process.StandardInput.FlushAsync();

        Assert.Equal("resumed", await process.StandardOutput.ReadLineAsync());

        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Harness_IgnoreYields_ShouldNotBlock()
    {
        // --ignore-yields models the timeout path: the harness announces the request but
        // does not wait, so it runs to completion with no ready token ever sent.
        using var process = StartHarness("emit:a;yield;emit:b;exit:0", ignoreYields: true);

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Contains("a", output);
        Assert.Contains(HarnessEntry.UpdateRequested, output);
        Assert.Contains("b", output);
        Assert.Equal(0, process.ExitCode);
    }

    private static Process StartHarness(string script, bool ignoreYields = false)
    {
        var dll = typeof(HarnessEntry).Assembly.Location;
        var apphost = OperatingSystem.IsWindows() ? Path.ChangeExtension(dll, ".exe") : Path.ChangeExtension(dll, null);

        // macOS: never spawn the copied apphost — same-named executables outside their
        // first-run location get SIGKILLed (see TestTools.SelfInvocation); the dotnet-host
        // branch below always runs.
        ProcessStartInfo psi;
        if (!OperatingSystem.IsMacOS() && apphost is not null && File.Exists(apphost) && apphost != dll)
        {
            psi = new ProcessStartInfo(apphost);
        }
        else
        {
            psi = new ProcessStartInfo("dotnet");
            psi.ArgumentList.Add(dll);
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        psi.ArgumentList.Add("--script");
        psi.ArgumentList.Add(script);
        if (ignoreYields)
        {
            psi.ArgumentList.Add("--ignore-yields");
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ScriptedAgentHarness.");
    }
}
