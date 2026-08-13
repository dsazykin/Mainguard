using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Orchestrator;
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
    /// <para><b>Opt-in.</b> It downloads the ~106 MiB pinned interpreter (measured, not remembered — the
    /// "~350 MB" this comment used to claim was the number used to justify fetching in the VM with
    /// <c>curl</c>, a program the VM does not have) and installs pytest from PyPI over the default-deny
    /// egress proxy. <c>MAINGUARD_VERIFY_E2E=1</c> runs it.</para>
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
        // No payload source override: this leg deliberately uses the SHIPPED HttpsToolchainPayloadSource,
        // so the ~106 MiB pinned interpreter is really fetched over HTTPS, really hashed against the
        // manifest pin, and really transferred through base64 — the exact sequence a user's install runs.
        var channel = new ToolchainChannel(
            new LocalCommandHost(Path.Combine(root, "stage")), log: _out.WriteLine, vmRoot: toolchainRoot);

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

        // ---- 3b. Egress: PyPI reachable THROUGH the proxy, and the proxy really constraining --------
        // "pip install worked" on its own does not prove the allowlist is doing anything — it is equally
        // consistent with the jail having open internet, in which case the whole default-deny story is
        // decoration. So both directions are measured against the SAME jail: an allowlisted registry must
        // answer, and a host that is not on the allowlist must not.
        var pypi = await fx.Engine.ExecAsync(containerId, new[]
        {
            "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", "--max-time", "25",
            "https://pypi.org/simple/pytest/",
        }, ct);
        _out.WriteLine($"pypi via proxy => exit {pypi.ExitCode}, http {pypi.Stdout.Trim()}");

        var blocked = await fx.Engine.ExecAsync(containerId, new[]
        {
            "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", "--max-time", "25",
            "https://example.com/",
        }, ct);
        _out.WriteLine($"non-allowlisted host => exit {blocked.ExitCode}, http {blocked.Stdout.Trim()}");

        Assert.Equal(0, pypi.ExitCode);
        Assert.Equal("200", pypi.Stdout.Trim());
        Assert.True(
            blocked.ExitCode != 0 || blocked.Stdout.Trim() is not "200",
            "a host that is NOT on the egress allowlist answered 200 from inside the jail — the jail has "
            + "a route that bypasses the default-deny proxy, which would make the pip result above "
            + $"meaningless (got exit {blocked.ExitCode}, http '{blocked.Stdout.Trim()}')");

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

    /// <summary>
    /// Runs the repository's verify command <b>the way production runs it</b>: resolved through the
    /// shipped <see cref="VerificationCommandResolver"/> and exec'd as bare argv.
    ///
    /// <para><b>This method used to build its own shell</b> — <c>["sh", "-c", "cd /workspace &amp;&amp; " +
    /// verifyCommand]</c> — and that made the whole test a lie. Production tokenises the command with a
    /// shell-free whitespace splitter and hands the argv straight to <c>docker exec</c>; a test that
    /// wraps it in <c>sh -c</c> first hands the repository a shell it will never actually have. Measured:
    /// a verify of <c>pip install -r requirements.txt &amp;&amp; python -m pytest -q</c> passes under the old
    /// harness and fails in production with <c>no such option: -m</c>, because pip swallows <c>&amp;&amp;</c>,
    /// <c>python</c> and <c>-m</c> as its own arguments. The instrument would have been green while every
    /// real Python repository was broken.</para>
    ///
    /// <para>The working directory comes from the container spec (<c>WORKDIR /workspace</c>), not from a
    /// <c>cd</c> the test prepends — for the same reason.</para>
    /// </summary>
    private static Task<SandboxExecResult> RunVerifyAsync(
        SandboxFixture fx, string containerId, string verifyCommand, CancellationToken ct)
    {
        var resolved = VerificationCommandResolver.Resolve(verifyCommand, verifyCommand);
        return fx.Engine.ExecAsync(containerId, resolved.Command, ct);
    }

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
        //
        // The `sh -c "…"` wrapper is NOT decoration and NOT a test convenience — it is the only form
        // that works. Verification tokenises this file with a shell-free whitespace splitter, so a bare
        // `a && b` arrives as argv `[a, &&, b]` and the first program swallows `&&` as its own argument
        // (measured: `no such option: -m`, exit 2). The tokeniser DOES honour quotes and drops the quote
        // characters, so this collapses to exactly three tokens — `sh`, `-c`, and the whole script — and
        // the shell that runs the `&&` is one the repository asked for explicitly.
        File.WriteAllText(
            Path.Combine(worktree, ".mainguard", "verify"),
            "sh -c \"pip install -q -r requirements.txt && python -m pytest -q\"\n");

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
        private readonly string _stageDir;

        public LocalCommandHost(string stageDir) => _stageDir = stageDir;

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

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The production host shells into the VM, so a missing binary comes back as a non-zero
                // exit rather than a throw. Launching directly, it throws. Matching the real host's
                // SHAPE here is the point: a fake that fails differently from production exercises a
                // codepath nobody runs. 127 is the shell's "command not found".
                return new AdapterCommandResult(127, string.Empty, ex.Message);
            }

            using (process)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return new AdapterCommandResult(process.ExitCode, stdout, stderr);
            }
        }

        public async Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, ct);
        }

        /// <summary>
        /// Materialises the hash-verified payload, mirroring <c>WslAdapterInstallHost</c>: base64 over a
        /// pipe into <c>tee</c>, decoded by the environment's own <c>base64 -d</c>.
        ///
        /// <para>This used to throw, on the theory that the channel "fetches and verifies in the VM".
        /// It did — with <c>curl</c>, which MainguardEnv does not have, so that path never once ran
        /// against a real environment; the throw was an instrument enforcing a design that could not
        /// work. Doing the real transfer here is what makes this test cover the shipped install.</para>
        /// </summary>
        public async Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
        {
            var safeName = string.Concat(fileName.Select(c =>
                char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-'));
            var b64Path = Path.Combine(_stageDir, safeName + ".b64");
            var finalPath = Path.Combine(_stageDir, safeName);

            Directory.CreateDirectory(_stageDir);
            await File.WriteAllTextAsync(b64Path, Convert.ToBase64String(content), ct);

            var decode = await RunAsync(
                new[] { "bash", "-c", $"base64 -d '{b64Path}' > '{finalPath}' && rm -f '{b64Path}'" }, ct);
            if (!decode.Succeeded)
                throw new InvalidOperationException(
                    $"Decoding the staged payload failed (exit {decode.ExitCode}): {decode.Stderr}");

            return finalPath;
        }
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
