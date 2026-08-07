using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The user-managed Python toolchain, end to end in a real hardened jail.
///
/// <para><b>The defect.</b> A repository whose <c>.mainguard/verify</c> is <c>python -m pytest -q</c>
/// reported "tests failed" — not because the tests failed, but because the jail had no pytest and no way
/// to get one: the agent base image bakes nixpkgs' <c>python3</c>, which ships <b>without pip</b>, and
/// the rootfs is read-only so apt cannot fix it. A Python repository could commit tests that could never
/// run, and the merge queue recorded that as failing code.</para>
///
/// <para><b>What is proven here, and why both directions.</b> A verifier that always passes is worse than
/// no verifier, so it is not enough to show a green run: the same jail, the same toolchain and the same
/// command must go RED when a test genuinely fails. Both directions run against one installed toolchain,
/// so a green result cannot come from the verification silently not happening.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class PythonToolchainDockerTests
{
    private const string RepoHandle = "python-e2e";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(30);

    private readonly ITestOutputHelper _out;

    public PythonToolchainDockerTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The premise, measured rather than asserted from memory: the base image's own interpreter cannot
    /// import pip. If this ever starts failing, the <c>python-3</c> entry's whole justification has
    /// changed and the entry deserves re-deciding rather than keeping by inertia.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheBaseImagesPython_CannotImportPip_WhichIsWhyThisToolchainExists()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync("agent-premise", ct: ct);
        var containerId = handle.ContainerId;

        var version = await fx.Engine.ExecAsync(containerId, new[] { "python3", "--version" }, ct);
        var pip = await fx.Engine.ExecAsync(containerId, new[] { "python3", "-c", "import pip" }, ct);

        _out.WriteLine($"base python3 --version => exit {version.ExitCode}: {version.Stdout.Trim()}");
        _out.WriteLine($"base import pip        => exit {pip.ExitCode}: {pip.Stderr.Trim()}");

        // There IS an interpreter — which is exactly why a bare `python3 --version` probe would have
        // reported this broken environment as healthy.
        Assert.Equal(0, version.ExitCode);
        // …and it has no pip, so nothing can be installed and no third-party test runner can ever run.
        Assert.NotEqual(0, pip.ExitCode);
    }

    /// <summary>
    /// The whole claim: a human installs the curated Python toolchain, a repository declares it, and its
    /// pytest suite verifies GREEN in a jail — then RED when a test actually fails.
    ///
    /// <para><b>Opt-in.</b> It downloads a ~350 MB pinned interpreter and installs pytest from PyPI over
    /// the default-deny egress proxy. <c>MAINGUARD_VERIFY_E2E=1</c> runs it.</para>
    /// </summary>
    [RequiresDockerAndOptInFact]
    public async Task APythonRepository_VerifiesGreenWhenItsTestsPass_AndRedWhenTheyFail()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;

        var root = E2ERoot();
        var toolchainRoot = Path.Combine(root, "toolchains");
        var worktree = Path.Combine(root, "workspace");

        // ---- 1. The install, through the SHIPPED channel ------------------------------------------
        // Not a hand-placed directory: the thing under test includes "does the product's own installer
        // put a working toolchain on disk", and a test that staged the files itself would answer a
        // different question.
        var channel = new ToolchainChannel(
            new LocalCommandHost(), log: _out.WriteLine, vmRoot: toolchainRoot);

        var installStopwatch = Stopwatch.StartNew();
        var installed = await channel.InstallAsync(
            "python-3", new Progress<string>(m => _out.WriteLine($"install: {m}")), ct);
        installStopwatch.Stop();

        _out.WriteLine($"installed      : {installed.IsInstalled} — {installed.Detail}");
        _out.WriteLine($"install wall   : {installStopwatch.Elapsed}");
        Assert.True(installed.IsInstalled, installed.Detail);

        // The marker exists only because the probe ran the interpreter and matched the pinned version.
        Assert.True(File.Exists(ToolchainPaths.RegistryMarkerPath("python-3", toolchainRoot)));

        // ---- 2. A repository with a real pytest suite ---------------------------------------------
        WritePythonRepo(worktree, testPasses: true);

        await using var fx = new SandboxFixture();
        var caches = new PackageCacheManager(root, log: _out.WriteLine);
        caches.Prepare(RepoHandle, "agent-1");
        var cacheDir = caches.PathFor(RepoHandle, "agent-1");

        var containerId = await CreateJailAsync(fx, toolchainRoot, worktree, cacheDir, ct);
        _out.WriteLine($"jail           : {containerId}");

        // ---- 3. The toolchain really reached the jail ---------------------------------------------
        // Through the product's own probe argv, resolved against the SANDBOX mount — so this asserts the
        // mount + PATH + environment wiring, not just that a directory exists on the host.
        var entry = channel.Manifest.TryGet("python-3")!;
        var probe = await fx.Engine.ExecAsync(
            containerId,
            entry.ProbeCommand(ToolchainPaths.SandboxInstallDir("python-3"), PackageCachePolicy.SandboxMount),
            ct);
        _out.WriteLine($"in-jail probe  => exit {probe.ExitCode}: {probe.Stdout.Trim()}{probe.Stderr.Trim()}");
        Assert.Equal(0, probe.ExitCode);
        Assert.Contains(entry.Version, probe.Stdout, StringComparison.Ordinal);

        // The declared toolchain must WIN the PATH race against the base image's pip-less python3 —
        // otherwise everything below would silently run the wrong interpreter.
        var which = await fx.Engine.ExecAsync(containerId, new[] { "sh", "-c", "command -v python3" }, ct);
        _out.WriteLine($"command -v python3 => {which.Stdout.Trim()}");
        Assert.StartsWith(ToolchainPaths.SandboxInstallDir("python-3"), which.Stdout.Trim(), StringComparison.Ordinal);

        // ---- 4. GREEN ------------------------------------------------------------------------------
        var verifyCommand = File.ReadAllText(Path.Combine(worktree, ".mainguard", "verify")).Trim();
        _out.WriteLine($"verify command : {verifyCommand}");

        var green = await RunVerifyAsync(fx, containerId, verifyCommand, ct);
        _out.WriteLine($"GREEN run exit : {green.ExitCode}");
        _out.WriteLine(Tail(green.Stdout));
        _out.WriteLine(Tail(green.Stderr));

        // pip had to reach PyPI through the default-deny proxy for this to be possible at all.
        Assert.DoesNotContain("No space left on device", green.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, green.ExitCode);
        Assert.Contains("passed", green.Stdout, StringComparison.Ordinal);

        // ---- 5. RED --------------------------------------------------------------------------------
        // Same jail, same toolchain, same command — only the repository's test changes. A verifier that
        // cannot go red is not a verifier.
        WritePythonRepo(worktree, testPasses: false);

        var red = await RunVerifyAsync(fx, containerId, verifyCommand, ct);
        _out.WriteLine($"RED run exit   : {red.ExitCode}");
        _out.WriteLine(Tail(red.Stdout));

        Assert.NotEqual(0, red.ExitCode);
        Assert.Contains("failed", red.Stdout, StringComparison.Ordinal);

        WriteToJobSummary("Python toolchain — verification both directions", new[]
        {
            $"toolchain : python-3 {entry.Version} ({installStopwatch.Elapsed} to install)",
            $"probe     : {probe.Stdout.Trim()}",
            $"green exit: {green.ExitCode}",
            $"red exit  : {red.ExitCode}",
        });
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static Task<SandboxExecResult> RunVerifyAsync(
        SandboxFixture fx, string containerId, string verifyCommand, CancellationToken ct) =>
        // `sh -c`, never `sh -lc`: a login shell sources /etc/profile, which on Debian resets PATH and
        // would drop the toolchain mount — the command would then fail at exit 127 for a reason that has
        // nothing to do with the repository.
        fx.Engine.ExecAsync(containerId, new[] { "sh", "-c", "cd /workspace && " + verifyCommand }, ct);

    private async Task<string> CreateJailAsync(
        SandboxFixture fx, string toolchainRoot, string worktree, string cacheDir, CancellationToken ct)
    {
        await fx.EnsureEgressReadyAsync(ct);
        var segment = await fx.Egress.EnsureAgentSegmentAsync(RepoHandle, "agent-1", ct);

        var create = ContainerSpecBuilder.Build(new ContainerSpecRequest(
            RepoHash: RepoHandle,
            AgentId: "agent-1",
            WorktreePath: worktree,
            ImageRef: fx.ImageRef,
            Limits: new SandboxLimits(2L * 1024 * 1024 * 1024, 1024, Cpus: 2),
            NetworkName: segment.NetworkName,
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: segment.ProxyUrl(EgressProxyConfigurator.ProxyPort)!,
            DnsServerAddress: segment.ProxyAddress,
            PackageCachePath: cacheDir,
            ToolchainsRootPath: toolchainRoot,
            ToolchainIds: new[] { "python-3" }));

        return await fx.CreateAndStartAsync(create, RepoHandle, "agent-1", ct);
    }

    /// <summary>
    /// A minimal but REAL Python repository: a dependency it must install from PyPI, a test pytest
    /// actually collects, and the declaration + verify command a user would commit.
    /// </summary>
    private static void WritePythonRepo(string worktree, bool testPasses)
    {
        Directory.CreateDirectory(Path.Combine(worktree, ".mainguard"));
        Directory.CreateDirectory(Path.Combine(worktree, "tests"));

        // Pinned, because an unpinned test dependency makes this test's own result depend on the day.
        File.WriteAllText(Path.Combine(worktree, "requirements.txt"), "pytest==8.3.4\n");

        // The repository declares a toolchain by ID and nothing else — no URL, no package list, no
        // command. That asymmetry is the security property, and it is what this file demonstrates.
        File.WriteAllText(Path.Combine(worktree, ".mainguard", "toolchain"), "python-3\n");

        // The install step is the repository's, exactly as `dotnet restore` is for a .NET repo: the
        // toolchain supplies the interpreter and pip, the repository supplies its own dependencies.
        File.WriteAllText(
            Path.Combine(worktree, ".mainguard", "verify"),
            "pip install -q -r requirements.txt && python -m pytest -q\n");

        File.WriteAllText(
            Path.Combine(worktree, "tests", "test_math.py"),
            testPasses
                ? "def test_adds():\n    assert 1 + 1 == 2\n"
                : "def test_adds():\n    assert 1 + 1 == 3\n");

        MakeJailWritable(worktree);
    }

    /// <summary>
    /// Runs the toolchain channel's commands on THIS machine. The channel's contract is
    /// <see cref="IAdapterInstallHost"/> — "run this argv where toolchains live" — and in production that
    /// is <c>wsl -d MainguardEnv --</c>. Here the toolchains live on the test box, so the same argv runs
    /// here. The commands, their order and every check between them are the shipped ones.
    /// </summary>
    private sealed class LocalCommandHost : IAdapterInstallHost
    {
        public async Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(command[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            for (var i = 1; i < command.Count; i++)
            {
                psi.ArgumentList.Add(command[i]);
            }

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new AdapterCommandResult(process.ExitCode, stdout, stderr);
        }

        public async Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, ct);
        }

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct) =>
            throw new InvalidOperationException(
                "The toolchain channel fetches and verifies in the VM; it must never stream a payload here.");
    }

    private static string E2ERoot()
    {
        var configured = Environment.GetEnvironmentVariable("MAINGUARD_E2E_ROOT");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mainguard-e2e", "python")
            : Path.Combine(configured, "python");

        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The jail runs as uid 1000 in its own user namespace; the worktree must be reachable by
    /// it. Not a security relaxation under test — MG-17's boot step owns this in production.</summary>
    private static void MakeJailWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add("a+rwX");
        psi.ArgumentList.Add(path);
        using var process = Process.Start(psi);
        process?.WaitForExit(30_000);
    }

    private static string Tail(string text, int max = 4000) =>
        string.IsNullOrEmpty(text) ? "(nothing)" : text.Length <= max ? text : text[^max..];

    private static void WriteToJobSummary(string title, IEnumerable<string> lines)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, $"### {title}\n\n```\n{string.Join('\n', lines)}\n```\n\n");
        }
        catch (IOException)
        {
        }
    }
}
