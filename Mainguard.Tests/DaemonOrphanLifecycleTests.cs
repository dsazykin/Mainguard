using System;
using System.IO;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The two halves of the orphaned-daemon fix.
///
/// <para><b>The incident.</b> A daemon launched on 2026-08-30 from the git worktree
/// <c>~/mg-work/sim</c> was still running fifteen days later, re-parented to init, holding loopback
/// 5250 against every later daemon. The worktree had been deleted on 2026-09-05. Nothing found it
/// because the only stop path pgrep'd for the CURRENT build's payload dll, and nothing stopped it
/// because the app's exit sequence has no daemon leg at all. The visible symptom was a bind failure
/// with a stack trace, from a brand-new daemon, on a machine the user believed had none running.</para>
/// </summary>
public class DaemonOrphanLifecycleTests
{
    // ---- DaemonPortHolder: identifying the squatter -------------------------------------------

    /// <summary>The real orphan's command line, as <c>ps -o command=</c> reported it.</summary>
    private const string RealOrphanCommandLine =
        "/usr/local/share/dotnet/dotnet "
        + "/Users/danielsazykin/mg-work/sim/Mainguard.Pro.App/bin/Release/net10.0/payload/daemon/Mainguard.Server.dll";

    [Fact]
    public void ThePayloadDll_IsExtractedFromARealDaemonCommandLine()
    {
        Assert.Equal(
            "/Users/danielsazykin/mg-work/sim/Mainguard.Pro.App/bin/Release/net10.0/payload/daemon/Mainguard.Server.dll",
            DaemonPortHolder.ExtractPayloadDll(RealOrphanCommandLine));
    }

    [Fact]
    public void ADaemonCommandLine_IsRecognisedAsOurs()
    {
        var holder = new DaemonPortHolder(31319, RealOrphanCommandLine);
        Assert.True(holder.IsMainguardDaemon);
    }

    [Fact]
    public void AStrangersProcess_IsNeverRecognisedAsOurs()
    {
        // The gate on offering to TERMINATE something: it must be identifiably a Mainguard daemon.
        var holder = new DaemonPortHolder(999, "/usr/bin/some-other-server --port 5250");
        Assert.False(holder.IsMainguardDaemon);
        Assert.Null(holder.PayloadDllPath);
        Assert.False(holder.PayloadIsGone);
    }

    [Fact]
    public void APayloadThatNoLongerExists_ReadsAsGone()
    {
        var holder = new DaemonPortHolder(31319, RealOrphanCommandLine);
        Assert.True(holder.PayloadIsGone); // that worktree is deleted — the whole point
    }

    [Fact]
    public void APayloadStillOnDisk_DoesNotReadAsGone()
    {
        var dll = Path.Combine(Path.GetTempPath(), $"mg-{Guid.NewGuid():N}", DaemonPortHolder.DaemonAssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllText(dll, "");
        try
        {
            var holder = new DaemonPortHolder(4242, $"/usr/bin/dotnet {dll}");
            Assert.False(holder.PayloadIsGone);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dll)!, recursive: true);
        }
    }

    /// <summary>
    /// A path the parser cannot read must read as "cannot prove it is stale", never as stale — the
    /// only decision this feeds is whether to offer to kill a process.
    /// </summary>
    [Fact]
    public void AnUnparseablePath_FailsSafeAsNotGone()
    {
        var holder = new DaemonPortHolder(
            7, $"/usr/bin/dotnet /Users/me/My Build Folder/{DaemonPortHolder.DaemonAssemblyName}");

        Assert.True(holder.IsMainguardDaemon);   // still identifiably ours
        Assert.False(holder.PayloadIsGone);      // but never asserted stale on a guess
    }

    [Fact]
    public void ARelativePath_IsRefusedRatherThanResolvedAgainstTheWrongCwd()
        => Assert.Null(DaemonPortHolder.ExtractPayloadDll($"dotnet ./{DaemonPortHolder.DaemonAssemblyName}"));

    // ---- The diagnosis verdict -----------------------------------------------------------------

    [Fact]
    public void ThePortConflictVerdict_IsResolvableOnlyWhenTheHolderIsOurs()
    {
        var ours = new DaemonConnectDiagnosis(DaemonConnectStage.PortHeldByForeignDaemon, "d")
        {
            Holder = new DaemonPortHolder(31319, RealOrphanCommandLine),
        };
        var stranger = new DaemonConnectDiagnosis(DaemonConnectStage.PortHeldByForeignDaemon, "d")
        {
            Holder = new DaemonPortHolder(999, "/usr/bin/postgres -p 5250"),
        };
        var noHolder = new DaemonConnectDiagnosis(DaemonConnectStage.PortHeldByForeignDaemon, "d");

        Assert.True(ours.IsResolvableByStoppingHolder);
        Assert.False(stranger.IsResolvableByStoppingHolder);
        Assert.False(noHolder.IsResolvableByStoppingHolder);

        // And it is NOT the app-performed repair — that one runs unasked, this one never does.
        Assert.False(ours.IsRepairableByDaemonRefresh);
    }

    [Fact]
    public void ThePortConflictBanner_NamesThePortAndTheEvidence()
    {
        var banner = new DaemonConnectDiagnosis(
            DaemonConnectStage.PortHeldByForeignDaemon, "It is process 31319, from a deleted build.").Banner;

        Assert.Contains("5250", banner, StringComparison.Ordinal);
        Assert.Contains("31319", banner, StringComparison.Ordinal);
    }
}
