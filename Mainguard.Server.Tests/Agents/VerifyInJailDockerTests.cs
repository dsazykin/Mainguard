using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-43 — the claim, end to end: <b>this repository's own <c>.mainguard/verify</c> completes inside a
/// real hardened jail</b>, with the toolchain layer #269 builds and the package cache this change adds.
///
/// <para>Everything else in the MG-43 suite measures a property of the cache. This measures the thing
/// the cache exists for. Before it, <c>dotnet test Mainguard.slnx --configuration Release</c> could not
/// finish in a jail at all: the 1.7 GB NuGet closure does not fit in the 256 MiB tmpfs <c>$HOME</c>, so
/// restore died at <c>ENOSPC</c> and the merge queue — the gate that decides whether an agent's work may
/// merge — recorded an ordinary failed verification.</para>
///
/// <para><b>Opt-in.</b> It is a full Release restore/build/test of the whole solution over a default-deny
/// egress proxy: minutes, and a real download. <c>MAINGUARD_VERIFY_E2E=1</c> runs it; every other run
/// skips through a <c>Skip</c>-setting attribute (xunit 2.9.3 has no working <c>Assert.Skip</c>).</para>
///
/// <para><b>Where the files go.</b> Not <c>Path.GetTempPath()</c>: on a WSL2 developer box <c>/tmp</c> is
/// a small tmpfs (measured: 4.9 GB total), and a Release build of this solution plus its package closure
/// does not fit — the test would fail for the same class of reason it exists to fix, on the wrong
/// filesystem. <c>MAINGUARD_E2E_ROOT</c> names an ext4 directory; it defaults under the user's home,
/// which is ext4 on the substrate this ships to.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class VerifyInJailDockerTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(90);

    private readonly ITestOutputHelper _out;

    public VerifyInJailDockerTests(ITestOutputHelper output) => _out = output;

    [RequiresDockerAndOptInFact]
    public async Task TheRepositorysVerifyCommand_CompletesInARealJail()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;

        var repoRoot = FindRepoRoot();
        var verifyCommand = File.ReadAllText(Path.Combine(repoRoot, ".mainguard", "verify")).Trim();
        var toolchainText = File.ReadAllText(Path.Combine(repoRoot, ".mainguard", "toolchain"));
        _out.WriteLine($"repo root      : {repoRoot}");
        _out.WriteLine($"verify command : {verifyCommand}");

        var e2eRoot = E2ERoot();
        var worktree = Path.Combine(e2eRoot, "workspace");

        // The cache comes from the SHIPPED manager, against an ordinary VM root with no group
        // provisioning — so the grant this run depends on is the product's, not a chmod the test did.
        // (The worktree still gets one below: making /workspace reachable is MG-3/MG-17's business, not
        // this test's, and there is no product code path that grants it outside the boot step.)
        var caches = new PackageCacheManager(e2eRoot, log: _out.WriteLine);
        var cacheUsage = caches.Prepare("verify-e2e", "agent-1");
        var cacheDir = caches.PathFor("verify-e2e", "agent-1");
        _out.WriteLine(cacheUsage.Describe());

        // A clean copy of the tracked tree — never the live worktree, and never with bin/obj carried in
        // (a stale Debug output is exactly the "MSBuild skipped the rebuild" trap this suite warns about).
        CopyTrackedTree(repoRoot, worktree, _out.WriteLine);
        MakeJailWritable(worktree);

        await using var fx = new SandboxFixture();

        // The layer #269 builds, from the base digest the spawn preflight resolves — the production path,
        // not a hand-rolled image.
        var baseDigest = await fx.Engine.ImageDigestAsync(fx.ImageRef, ct);
        var declaration = ToolchainDeclarationResolver.Parse(toolchainText, "verify-e2e");
        var provisioner = new ToolchainProvisioner(new DockerToolchainImageBuilder(fx.Docker), _out.WriteLine);
        var layer = await provisioner.EnsureAsync("verify-e2e", declaration, baseDigest, ct);
        Assert.NotNull(layer);
        _out.WriteLine($"toolchain layer: {layer!.ImageRef} (base {layer.BaseDigest})");

        // A build jail's ceilings. `SandboxLimits.Default` is 2 GiB / 512 pids / 2 CPUs, sized for an
        // agent CLI; a Release build of a 15-project solution is not that. Reported rather than hidden,
        // because "what does verification actually need?" is a real operational answer and this test is
        // the only place that measures it.
        //
        // Measured, and the reason these numbers are what they are: at 6 CPUs / 8 GiB the run died at
        // exit 137 after 6m52s. That is SIGKILL, not a test failure — MSBuild sizes its node count from
        // the CPU ceiling, so 6 CPUs means ~6 build nodes plus VSTest hosts, and the box's whole Docker
        // VM has 9.7 GB. The container limit was never satisfiable and the HOST OOM killer fired first.
        // Sized down to 4 CPUs the peak fits, and the ceiling now binds inside the cgroup where it can
        // be diagnosed, instead of outside it where it looks like the jail vanished.
        var limits = new SandboxLimits(
            MemoryBytes: 5L * 1024 * 1024 * 1024, Pids: 2048, Cpus: 4, NoFile: 16384, NProc: 8192);

        var (containerId, segment) = await CreateVerifyJailAsync(fx, layer.ImageRef, worktree, cacheDir, limits, ct);
        _out.WriteLine($"jail           : {containerId} on {segment.NetworkName} (proxy {segment.ProxyAddress})");

        // Proof the cache is really there before spending an hour finding out otherwise.
        var cacheProbe = await fx.Engine.ExecAsync(containerId, PackageCachePolicy.WritabilityProbe(), ct);
        Assert.Null(PackageCachePolicy.DescribeProbeFailure(cacheProbe.Stdout, cacheProbe.ExitCode));

        var toolchainProbe = await fx.Engine.ExecAsync(containerId, new[] { "dotnet", "--version" }, ct);
        _out.WriteLine($"dotnet --version => {toolchainProbe.Stdout.Trim()}");
        Assert.Equal(0, toolchainProbe.ExitCode);

        // ---- the measurement -------------------------------------------------------------------------
        var stopwatch = Stopwatch.StartNew();
        var run = await fx.Engine.ExecAsync(
            // `sh -c`, never `sh -lc`: a login shell sources /etc/profile, which on Debian resets PATH
            // and would drop the toolchain layer's /opt/mainguard-toolchain entry — the command would
            // then fail at exit 127 for a reason that has nothing to do with the cache.
            containerId, new[] { "sh", "-c", "cd /workspace && " + verifyCommand }, ct);
        stopwatch.Stop();

        _out.WriteLine($"verify exit    : {run.ExitCode}");
        _out.WriteLine($"verify wall    : {stopwatch.Elapsed}");
        _out.WriteLine(Tail(run.Stdout, 12000));
        _out.WriteLine(Tail(run.Stderr, 4000));

        var usedInCache = await fx.Engine.ExecAsync(
            containerId, new[] { "sh", "-c", $"du -sh {PackageCachePolicy.SandboxMount} 2>/dev/null" }, ct);
        var usedInHome = await fx.Engine.ExecAsync(
            containerId, new[] { "sh", "-c", $"du -sh {ContainerSpecBuilder.AgentHome} 2>/dev/null" }, ct);
        _out.WriteLine($"cache holds    : {usedInCache.Stdout.Trim()}");
        _out.WriteLine($"$HOME holds    : {usedInHome.Stdout.Trim()}");

        WriteToJobSummary("MG-43 — .mainguard/verify inside a real jail", new[]
        {
            $"command : {verifyCommand}",
            $"image   : {layer.ImageRef}",
            $"exit    : {run.ExitCode}",
            $"wall    : {stopwatch.Elapsed}",
            $"cache   : {usedInCache.Stdout.Trim()}",
            $"home    : {usedInHome.Stdout.Trim()}",
        });

        // The failure this closes has a NAME, and it is not "some tests failed": it is the restore
        // running out of space. Assert on that specifically as well as on the exit code, so a regression
        // is diagnosable from the assertion message alone.
        Assert.DoesNotContain("No space left on device", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No space left on device", run.Stderr, StringComparison.Ordinal);

        // 137 is SIGKILL — the jail was killed, it did not fail. Called out separately because "exit
        // != 0" would report it as a failing verification, which is the exact confusion this whole
        // subsystem exists to prevent: a resourcing problem must never read as a verdict on the code.
        Assert.True(run.ExitCode != 137,
            $"the jail was KILLED after {stopwatch.Elapsed} (exit 137), not failed: the build's peak "
            + $"memory exceeded what the host could back for a {limits.MemoryBytes / (1024 * 1024)} MiB / "
            + $"{limits.Cpus} CPU ceiling. This is a resourcing result, not a verification result.");

        Assert.Equal(0, run.ExitCode);
    }

    // ---- harness --------------------------------------------------------------------------------------

    private static async Task<(string ContainerId, AgentSegment Segment)> CreateVerifyJailAsync(
        SandboxFixture fx, string imageRef, string worktree, string cacheDir, SandboxLimits limits, CancellationToken ct)
    {
        await fx.EnsureEgressReadyAsync(ct);
        var segment = await fx.Egress.EnsureAgentSegmentAsync("verify-e2e", "agent-1", ct);

        var create = ContainerSpecBuilder.Build(new ContainerSpecRequest(
            RepoHash: "verify-e2e",
            AgentId: "agent-1",
            WorktreePath: worktree,
            ImageRef: imageRef,
            Limits: limits,
            NetworkName: segment.NetworkName,
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: segment.ProxyUrl(EgressProxyConfigurator.ProxyPort)!,
            DnsServerAddress: segment.ProxyAddress,
            PackageCachePath: cacheDir));

        return (await fx.CreateAndStartAsync(create, "verify-e2e", "agent-1", ct), segment);
    }

    /// <summary>The repo root, found by walking up for <c>.mainguard/verify</c> from the test binary.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".mainguard", "verify")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
               ?? throw new InvalidOperationException("could not locate the repository root (.mainguard/verify)");
    }

    private static string E2ERoot()
    {
        var configured = Environment.GetEnvironmentVariable("MAINGUARD_E2E_ROOT");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mainguard-verify-e2e")
            : configured;

        // The cache is DELIBERATELY kept between runs unless MAINGUARD_E2E_COLD=1 asks for a cold one.
        // Wiping by default would make every run a full 1.7 GB download and would measure the opposite
        // of what the feature is for. It also matters for reliability, measured: on a cold run the two
        // ~150 MB runtime packs (`Microsoft.NETCore.App.Runtime.*`) are the packages most likely to
        // stall through tinyproxy, and a run that inherits them from the previous cache never asks for
        // them at all — which is precisely the value a package cache delivers to a merge queue that
        // verifies the same repository over and over.
        if (Environment.GetEnvironmentVariable("MAINGUARD_E2E_COLD") == "1" && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        // The workspace, unlike the cache, is always replaced — see CopyTrackedTree.
        var workspace = Path.Combine(root, "workspace");
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Copies the repository's TRACKED files into <paramref name="destination"/> via <c>git archive</c>,
    /// so no <c>bin/</c>, <c>obj/</c> or <c>.git</c> comes along. Two reasons, both learned the hard way:
    /// a carried-in Debug output makes MSBuild skip work the run is supposed to do, and a copied
    /// <c>.git</c> would put a writable git dir inside <c>/workspace</c>, which is not the shape MG-3
    /// ships.
    /// </summary>
    private static void CopyTrackedTree(string repoRoot, string destination, Action<string> log)
    {
        Directory.CreateDirectory(destination);
        var psi = new ProcessStartInfo("sh")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"git archive --format=tar HEAD | tar -x -C '{destination}'");

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        log($"git archive => exit {process.ExitCode} {stderr.Trim()}");
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"could not stage the tracked tree: {stderr}");
        }
    }

    /// <summary>
    /// 0777 on the tree the jail must write. In production this grant is the MG-17 group whose gid IS the
    /// remapped agent gid, provisioned at boot — a VM-level fact a test process cannot reproduce (the
    /// daemon itself cannot chown into the remapped range). Making the mode permissive removes the
    /// runner's uid mapping from the question so this test measures the CACHE rather than the box.
    /// </summary>
    private static void MakeJailWritable(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var psi = new ProcessStartInfo("chmod") { RedirectStandardError = true };
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add("a+rwX");
        psi.ArgumentList.Add(root);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    private static string Tail(string? text, int max)
    {
        var s = (text ?? string.Empty).TrimEnd();
        return s.Length <= max ? s : "…" + s[^max..];
    }

    private static void WriteToJobSummary(string heading, IReadOnlyList<string> lines)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, $"### {heading}\n\n```\n{string.Join("\n", lines)}\n```\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
