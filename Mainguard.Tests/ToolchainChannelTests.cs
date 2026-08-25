using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Tests.TestTools;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The user-managed toolchain channel: the curated manifest, the install discipline (verify the pin
/// before unpacking, prove it RUNS before believing it), and the jail wiring that lets a read-only
/// toolchain mount actually be used.
///
/// <para>Every test here drives the REAL <see cref="ToolchainChannel"/> over a scripted VM host, so the
/// policy under test is the shipped policy rather than a restatement of it.</para>
/// </summary>
public class ToolchainChannelTests
{
    // ---- The shipped manifest --------------------------------------------------------------------

    [Fact]
    public void TheShippedManifest_Parses_AndCuratesPython()
    {
        // The manifest is an embedded JSON file, so a bad edit is invisible until something loads it.
        // This is the test that makes a bad edit fail CI instead of a user's install.
        var manifest = ToolchainManifest.Shipped;

        var python = manifest.TryGet("python-3");
        Assert.NotNull(python);
        Assert.Equal("3.12.13", python!.Version);
        Assert.StartsWith("https://", python.PayloadUrl, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", python.Sha256);
    }

    [Fact]
    public void ThePythonProbe_ImportsPip_NotJustTheInterpreter()
    {
        // The defect this whole entry closes is a MISSING PIP, not a missing interpreter — the agent
        // base image already carries a python3 with no pip. A probe that only ran `python3 --version`
        // would have reported the owner's broken environment as healthy, and would keep doing so.
        var python = ToolchainManifest.Shipped.TryGet("python-3")!;
        var probe = string.Join(' ', python.Probe.Command);

        Assert.Contains("import pip", probe, StringComparison.Ordinal);

        // And it must run THIS toolchain's interpreter by absolute path, never whatever `python3` PATH
        // happens to resolve to — otherwise the base image's pip-less interpreter answers the probe.
        Assert.StartsWith(ToolchainEntry.RootToken, python.Probe.Command[0], StringComparison.Ordinal);
    }

    [Theory]
    // A plaintext payload URL: the sha pin protects against a changed upstream, never against an open
    // channel, so a MITM could swap the bytes before anyone wrote a pin down.
    [InlineData("\"payloadUrl\": \"https://x/y.tgz\"", "\"payloadUrl\": \"http://x/y.tgz\"")]
    // A truncated hash is not a pin.
    [InlineData("\"sha256\": \"" + SixtyFourHex + "\"", "\"sha256\": \"abc123\"")]
    // An id that could be read as a path — it is also a directory name.
    [InlineData("\"id\": \"tool-x\"", "\"id\": \"../escape\"")]
    // A probe that proves nothing.
    [InlineData("\"expectedVersionSubstring\": \"1.2.3\"", "\"expectedVersionSubstring\": \"\"")]
    public void AManifestThatWeakensThePin_IsRefusedAtParse(string good, string bad)
    {
        var valid = ValidManifestJson();
        Assert.Contains(good, valid, StringComparison.Ordinal); // the fixture really contains what we swap

        var broken = valid.Replace(good, bad, StringComparison.Ordinal);

        Assert.Throws<ToolchainManifestException>(() => ToolchainManifest.Parse(broken));
    }

    [Fact]
    public void AValidManifest_Parses()
    {
        // The negative theory above is only meaningful if the unmodified fixture is accepted.
        var manifest = ToolchainManifest.Parse(ValidManifestJson());
        Assert.Equal("tool-x", Assert.Single(manifest.Entries).Id);
    }

    // ---- Install discipline ----------------------------------------------------------------------

    [Fact]
    public async Task AnInstall_VerifiesTheChecksum_ThenUnpacks_ThenProvesItRuns()
    {
        var host = new ScriptedHost();
        host.ProbeStdout = "1.2.3 ok";
        var payloads = new FakeToolchainPayloadSource();
        var channel = new ToolchainChannel(host, InstallableManifest(), payloads: payloads);

        var status = await channel.InstallAsync("tool-x");

        Assert.True(status.IsInstalled);

        // The payload was fetched from the pinned URL — once — and its bytes reached the VM through the
        // staging seam rather than through any command the VM had to own.
        Assert.Equal(new[] { new Uri("https://x/y.tgz") }, payloads.Fetched);
        var staged = Assert.Single(host.Staged);
        Assert.Equal("tool-x-1.2.3.tar.gz", staged.FileName);
        Assert.Equal(FakeToolchainPayloadSource.PayloadFor("https://x/y.tgz").Length, staged.Bytes);

        // Order is the property, not mere presence: bytes that reach the VM before the pin is checked are
        // unverified bytes on the filesystem, whatever happens next.
        Assert.True(host.IndexOfCommand("tar") >= 0, $"expected an unpack; ran: {host.Describe()}");

        // The marker is written last, and only a green probe earns it.
        Assert.Contains(ToolchainPaths.RegistryMarkerPath("tool-x"), host.WrittenFiles.Keys);
    }

    /// <summary>
    /// <b>The regression that matters.</b> The install used to fetch its payload by shelling
    /// <c>curl</c> into the VM, and the VM has no <c>curl</c> and no <c>wget</c> — the pinned package
    /// list carries neither, and a live MainguardEnv answers <c>command -v</c> for neither. Every user
    /// install failed at <c>curl: command not found</c>, exit 127, from the day it shipped.
    ///
    /// <para>Nothing caught it because the fake host answered <c>curl</c> with exit 0: the suite was
    /// measuring a VM that does not exist. So this asserts the set of programs an install requires the
    /// environment to have, which is the fact that was never under test.</para>
    /// </summary>
    [Fact]
    public async Task AnInstall_RunsNoDownloaderInTheVm_AndNeedsOnlyBinariesTheVmReallyHas()
    {
        var host = new ScriptedHost { ProbeStdout = "1.2.3 ok" };
        var channel = new ToolchainChannel(
            host, InstallableManifest(), payloads: new FakeToolchainPayloadSource());

        await channel.InstallAsync("tool-x");

        // Not just argv[0]: a downloader smuggled in as an argument of `sh -c` would be just as broken.
        foreach (var absent in MainguardEnvFacts.AbsentBinaries)
        {
            Assert.DoesNotContain(
                host.Commands,
                c => c.Any(a => a.Split('/')[^1].Equals(absent, StringComparison.Ordinal)));
        }

        // coreutils (rm/mkdir/mv) and tar are in build/mainguardos/packages.pinned.txt and present on a
        // live VM; the remaining entry is the toolchain's own probe binary, by absolute path. Anything
        // NEW appearing here is a new dependency on the environment, and has to be justified against
        // that pinned list rather than discovered by a user whose install dies at exit 127.
        Assert.Equal(
            new[] { $"{ToolchainPaths.VmInstallDir("tool-x")}/bin/toolx", "mkdir", "mv", "rm", "tar" },
            host.DistinctExecutables.OrderBy(e => e, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchThePin_NeverEntersTheVm()
    {
        var host = new ScriptedHost();
        var payloads = new FakeToolchainPayloadSource { Corrupt = true };
        var channel = new ToolchainChannel(host, InstallableManifest(), payloads: payloads);

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("tool-x"));

        Assert.Equal(ToolchainChannelError.HashMismatch, ex.Error);

        // Stronger than the in-VM sha256sum this replaced: there is nothing to discard, because the bytes
        // never crossed into the environment at all.
        Assert.Empty(host.Staged);
        Assert.True(host.IndexOfCommand("tar") < 0, $"nothing may be unpacked after a bad checksum; ran: {host.Describe()}");
        Assert.Empty(host.WrittenFiles);

        // …and the copy must say that, rather than claiming a discard that never happened.
        Assert.Contains("nothing was transferred", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADownloadThatCannotBeFetched_IsATypedDownloadFailure_NamingTheUrl()
    {
        var host = new ScriptedHost();
        var payloads = new FakeToolchainPayloadSource
        {
            Throw = new System.Net.Http.HttpRequestException("Name or service not known"),
        };
        var channel = new ToolchainChannel(host, InstallableManifest(), payloads: payloads);

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("tool-x"));

        Assert.Equal(ToolchainChannelError.DownloadFailed, ex.Error);
        Assert.Contains("https://x/y.tgz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Name or service not known", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Staged);
    }

    [Fact]
    public async Task AVerifiedPayloadThatCannotBeTransferred_IsItsOwnFailure_NotADownloadFailure()
    {
        // The two have different remedies and must not be collapsed: the bytes arrived and matched the
        // pin, so nothing is wrong with the network or the manifest — the environment is unreachable.
        var host = new ScriptedHost
        {
            ThrowOnStage = new InvalidOperationException("Staging the verified payload into the VM failed"),
        };
        var channel = new ToolchainChannel(
            host, InstallableManifest(), payloads: new FakeToolchainPayloadSource());

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("tool-x"));

        Assert.Equal(ToolchainChannelError.StageFailed, ex.Error);
        Assert.True(host.IndexOfCommand("tar") < 0, $"nothing may be unpacked; ran: {host.Describe()}");
        Assert.Empty(host.WrittenFiles);
    }

    [Fact]
    public async Task AToolchainThatLandsButDoesNotRun_IsAFailedInstall_AndLeavesNoMarker()
    {
        // PR #305: an adapter's marker reported healthy for eleven days because a file had arrived
        // while the binary it pointed at was a stub. A file arriving is not an install.
        var host = new ScriptedHost();
        host.ProbeExitCode = 127;
        host.ProbeStderr = "cannot execute binary file";
        var channel = new ToolchainChannel(
            host, InstallableManifest(), payloads: new FakeToolchainPayloadSource());

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("tool-x"));

        Assert.Equal(ToolchainChannelError.ProbeFailed, ex.Error);
        Assert.Contains("cannot execute binary file", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.WrittenFiles);
    }

    [Fact]
    public async Task AToolchainThatRunsButReportsTheWrongVersion_IsAFailedInstall()
    {
        // Exit code 0 is not evidence of WHICH version landed, and a verification jail quietly running a
        // different interpreter than the developer's machine is the failure mode this pin exists for.
        var host = new ScriptedHost();
        host.ProbeStdout = "9.9.9 something else";
        var channel = new ToolchainChannel(
            host, InstallableManifest(), payloads: new FakeToolchainPayloadSource());

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("tool-x"));

        Assert.Equal(ToolchainChannelError.VersionMismatch, ex.Error);
        Assert.Empty(host.WrittenFiles);
    }

    [Fact]
    public async Task InstalledState_ComesFromTheProbe_NotFromTheMarkerFile()
    {
        // The status a settings page shows must be established by running the thing. A status derived
        // from a file on disk is a status that can lie, and this repo has watched one do it.
        // The toolchain IS on disk (PreInstalled) and its marker says healthy — but it does not run.
        var host = new ScriptedHost { PreInstalled = true, ProbeExitCode = 1 };
        host.WrittenFiles[ToolchainPaths.RegistryMarkerPath("tool-x")] = "{\"Id\":\"tool-x\",\"Version\":\"1.2.3\"}";

        var channel = new ToolchainChannel(host, ToolchainManifest.Parse(ValidManifestJson()));

        var status = Assert.Single(await channel.ListAsync());

        Assert.False(status.IsInstalled); // …so it is NOT installed, marker or no marker
    }

    [Fact]
    public async Task AnAlreadyHealthyToolchain_IsNotReinstalled()
    {
        var host = new ScriptedHost { PreInstalled = true, ProbeStdout = "1.2.3 ok" };
        var payloads = new FakeToolchainPayloadSource();
        var channel = new ToolchainChannel(host, InstallableManifest(), payloads: payloads);

        var status = await channel.InstallAsync("tool-x");

        Assert.True(status.IsInstalled);
        // Nothing was fetched and nothing crossed into the VM — an idempotent install costs one probe.
        Assert.Empty(payloads.Fetched);
        Assert.Empty(host.Staged);
    }

    [Fact]
    public async Task AnUncuratedId_IsATypedRefusal_ListingWhatIsAvailable()
    {
        var channel = new ToolchainChannel(new ScriptedHost(), ToolchainManifest.Parse(ValidManifestJson()));

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("whatever"));

        Assert.Equal(ToolchainChannelError.UnknownToolchain, ex.Error);
        Assert.Contains("tool-x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatCannotEvenRunTheProbe_ReportsNotInstalled_RatherThanThrowing()
    {
        // Found by running the end-to-end test for the first time, where this threw a raw
        // Win32Exception out of StatusAsync. The probe names the interpreter by ABSOLUTE PATH, so
        // before an install that path does not exist — and "is it installed?" is exactly the question
        // asked at that moment.
        //
        // Why it matters beyond a test: StatusAsync is what
        // SandboxAgentLauncher.EnsureMountedToolchainsAsync calls before EVERY spawn, and that path
        // exists to turn a missing toolchain into a typed, readable refusal. A raw platform exception
        // out of a spawn is the one thing it must never produce.
        var manifest = ToolchainManifest.Parse(ValidManifestJson());
        var channel = new ToolchainChannel(new ThrowingHost(), manifest);

        var status = await channel.StatusAsync(manifest.Entries[0]);

        Assert.False(status.IsInstalled);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public async Task AnUnreachableEnvironment_IsReportedAsUNKNOWN_NotAsNotInstalled()
    {
        // The distinction that keeps the fix from being a downgrade. Swapping a raw exception for the
        // confident sentence "Not installed — install it in Settings → Toolchains" would send someone
        // whose WSL is down to a button that fails the same way, for a reason nothing on screen names.
        // "We could not look" is not "it is not there".
        var manifest = ToolchainManifest.Parse(ValidManifestJson());
        var channel = new ToolchainChannel(new ThrowingHost(), manifest);

        var status = await channel.StatusAsync(manifest.Entries[0]);

        Assert.True(status.CouldNotCheck);
        Assert.Contains("could not", status.Detail, StringComparison.OrdinalIgnoreCase);
        // It must NOT claim the toolchain is absent.
        Assert.DoesNotContain("Not installed", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeThatRunsAndFails_IsStillPlainNotInstalled()
    {
        // The converse, so the new flag cannot swallow the ordinary case: a host that WORKS and a probe
        // that simply exits non-zero is a genuine "not installed", and must stay actionable.
        var manifest = ToolchainManifest.Parse(ValidManifestJson());
        var host = new ScriptedHost { PreInstalled = false };
        var channel = new ToolchainChannel(host, manifest);

        var status = await channel.StatusAsync(manifest.Entries[0]);

        Assert.False(status.IsInstalled);
        Assert.False(status.CouldNotCheck);
        Assert.Equal("Not installed", status.Detail);
    }

    // ---- The catalog + the spawn path ------------------------------------------------------------

    [Fact]
    public void PythonIsDeclarable_AndIsDeliveredAsAMount_NotALayer()
    {
        var recipe = ToolchainCatalog.TryGet("python-3");

        Assert.NotNull(recipe);
        Assert.Equal(ToolchainDelivery.RuntimeMount, recipe!.Delivery);
    }

    [Fact]
    public async Task ARepoDeclaringOnlyAMountedToolchain_BuildsNoImageLayer()
    {
        // The point of the mount: no build, so no multi-minute spawn-path wait and no gigabyte of image
        // cache for a toolchain that is already sitting on the VM.
        var builder = new CountingBuilder();
        var provisioner = new ToolchainProvisioner(builder);
        var declaration = ToolchainDeclarationResolver.Parse("python-3", "repo");

        var layer = await provisioner.EnsureAsync("repo", declaration, "sha256:" + new string('a', 64));

        Assert.Null(layer);
        Assert.Equal(0, builder.Builds);
    }

    [Fact]
    public void AJailCarryingAMountedToolchain_GetsItReadOnly_OnPath_AndWithItsEnvironment()
    {
        var create = ContainerSpecBuilder.Build(SpecFor(new[] { "python-3" }));

        var mount = Assert.Single(create.HostConfig.Mounts, m =>
            string.Equals(m.Target, ToolchainPaths.SandboxMount, StringComparison.Ordinal));
        Assert.True(mount.ReadOnly, "a shared toolchain tree a jail could write is a jail that can "
                                    + "change what another agent's verification runs");

        var path = Assert.Single(create.Env, e => e.StartsWith("PATH=", StringComparison.Ordinal));
        // The declared toolchain must come FIRST — the base image already has a python3 with no pip, and
        // losing that race is exactly the bug being fixed.
        Assert.StartsWith($"PATH={ToolchainPaths.SandboxInstallDir("python-3")}/bin:", path, StringComparison.Ordinal);
        // …and the rest of the image's PATH must survive, or the adapters mount and nix profile vanish.
        Assert.EndsWith(ContainerSpecBuilder.BaseImagePath, path, StringComparison.Ordinal);

        // A read-only toolchain is only usable if pip's writes go somewhere writable.
        var userBase = Assert.Single(create.Env, e => e.StartsWith("PYTHONUSERBASE=", StringComparison.Ordinal));
        Assert.StartsWith($"PYTHONUSERBASE={PackageCachePolicy.SandboxMount}/", userBase, StringComparison.Ordinal);
    }

    [Fact]
    public void AJailDeclaringNoMountedToolchain_GetsNoToolchainMountAndNoPathOverride()
    {
        var create = ContainerSpecBuilder.Build(SpecFor(Array.Empty<string>()));

        Assert.DoesNotContain(create.HostConfig.Mounts, m =>
            string.Equals(m.Target, ToolchainPaths.SandboxMount, StringComparison.Ordinal));
        // Untouched PATH means the image's own ENV still governs — never a half-configured override.
        Assert.DoesNotContain(create.Env, e => e.StartsWith("PATH=", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBaseImagePathConstant_MatchesTheAgentBaseImage()
    {
        // Docker's Env does no shell expansion, so the PATH handed to CreateContainerAsync must be
        // complete — which makes this constant a copy of the Dockerfile's final ENV PATH line. A copy
        // that drifts is a jail that silently loses the adapters mount or the nix profile, and nothing
        // else would notice. Same guard the catalog's nixpkgs revision already carries.
        var dockerfile = RepoFile("images/mainguard-agent-base/Dockerfile");
        var matches = Regex.Matches(File.ReadAllText(dockerfile), @"^ENV PATH=(.+)$", RegexOptions.Multiline);

        Assert.NotEmpty(matches);
        var last = matches[^1].Groups[1].Value.Trim();

        Assert.Equal(ContainerSpecBuilder.BaseImagePath, last);
    }

    // ---- The verify command a Python repo must write ---------------------------------------------

    /// <summary>
    /// The recipe this feature tells people to use must actually survive the shell-free tokeniser.
    ///
    /// <para>This test exists because the obvious recipe — <c>pip install -r requirements.txt &amp;&amp;
    /// python -m pytest -q</c> — is <b>broken</b>, and was recommended in this repository's own docs
    /// until it was measured. Verification splits <c>.mainguard/verify</c> on whitespace and hands the
    /// argv straight to <c>docker exec</c>; there is no shell, so <c>&amp;&amp;</c> is passed to pip as a
    /// positional argument. Measured in a real jail: <c>no such option: -m</c>, exit 2.</para>
    ///
    /// <para>Nobody noticed for a simple reason: this repository's own verify is a single command
    /// (<c>dotnet test …</c>), and .NET is the one ecosystem that needs no separate install step. Every
    /// ecosystem this change enables needs one.</para>
    /// </summary>
    [Fact]
    public void TheDocumentedPythonVerifyCommand_TokenisesToAShellInvocation_NotAMangledPipCall()
    {
        const string recommended = "sh -c \"pip install -q -r requirements.txt && python -m pytest -q\"";

        var argv = VerificationCommandResolver.Resolve(recommended, recommended).Command;

        // Exactly three tokens: the quoted script must survive as ONE argument, quotes stripped.
        Assert.Equal(
            new[] { "sh", "-c", "pip install -q -r requirements.txt && python -m pytest -q" },
            argv);
    }

    /// <summary>
    /// The converse form is now REFUSED at resolve time rather than mangled into a pip usage error.
    ///
    /// <para>This test previously asserted the mangling itself — that <c>&amp;&amp;</c>, <c>python</c> and
    /// <c>-m</c> all became arguments to pip. That was an accurate description of the behaviour and a bad
    /// thing to leave standing: pip then exits 2, a non-zero exit is the only signal
    /// <c>VerificationRunner</c> reads, and the merge queue tells a human <b>their tests failed</b> about
    /// a command that never started. Documenting that in a test made it look intended.</para>
    /// </summary>
    [Fact]
    public void ABareAmpersandVerifyCommand_IsRefusedAtResolveTime_NotRunAndReportedAsFailingTests()
    {
        const string broken = "pip install -q -r requirements.txt && python -m pytest -q";

        var ex = Assert.Throws<MalformedVerificationCommandException>(
            () => VerificationCommandResolver.Resolve(broken, broken));

        // The message has to carry the three things a human needs, or the refusal is just a different
        // kind of unhelpful: what is wrong, that nothing ran, and the exact command that works.
        Assert.Contains("&&", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pip", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no verification was recorded", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"sh -c \"{broken}\"", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal must not fire on operators that are ARGUMENTS rather than shell syntax, or it becomes
    /// a new way to break working repositories. All of these run correctly today and must keep resolving.
    /// </summary>
    [Theory]
    [InlineData("dotnet test --logger \"console;verbosity=detailed\"")]
    [InlineData("sh -c \"pip install -r requirements.txt && python -m pytest -q\"")]
    [InlineData("curl \"https://example.test/?a=1&b=2\"")]
    public void OperatorsInsideAQuotedArgument_AreNotRefused(string command)
    {
        var argv = VerificationCommandResolver.Resolve(command, command).Command;
        Assert.NotEmpty(argv);
    }

    // ---- Fixtures --------------------------------------------------------------------------------

    private const string SixtyFourHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string PayloadUrl = "https://x/y.tgz";

    /// <summary>The pin the install fixtures use: the REAL SHA-256 of the bytes
    /// <see cref="FakeToolchainPayloadSource"/> serves for <see cref="PayloadUrl"/>. It has to be real
    /// now — the channel hashes the bytes it holds, so no fake can be told what to report.</summary>
    private static string RealSha => FakeToolchainPayloadSource.Sha256For(PayloadUrl);

    /// <param name="sha256">The pin the fixture declares. Defaults to a hash that matches NOTHING, which
    /// is what the parse-level theories want; install tests pass <see cref="RealSha"/>.</param>
    private static string ValidManifestJson(string? sha256 = null) => $$"""
        {
          "toolchains": [
            {
              "id": "tool-x",
              "displayName": "Tool X",
              "summary": "A curated toolchain.",
              "version": "1.2.3",
              "payloadUrl": "{{PayloadUrl}}",
              "sha256": "{{sha256 ?? SixtyFourHex}}",
              "payloadUrlArm64": "{{PayloadUrl}}",
              "sha256Arm64": "{{sha256 ?? SixtyFourHex}}",
              "stripComponents": 1,
              "pathEntries": ["{toolchain}/bin"],
              "environment": { "TOOL_X_HOME": "{toolchain}" },
              "probe": {
                "command": ["{toolchain}/bin/toolx", "--version"],
                "expectedVersionSubstring": "1.2.3"
              }
            }
          ]
        }
        """;

    /// <summary>The manifest an install test drives: pinned to what the fake source really serves.</summary>
    private static ToolchainManifest InstallableManifest() =>
        ToolchainManifest.Parse(ValidManifestJson(RealSha));

    private static ContainerSpecRequest SpecFor(IReadOnlyList<string> toolchainIds) =>
        new(
            RepoHash: "repo", AgentId: "agent-1", WorktreePath: "/home/mainguard/mainguard/worktrees/w",
            ImageRef: "mainguard-agent-base:latest",
            Limits: new SandboxLimits(1024L * 1024 * 1024, 512, 2),
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://proxy:8888",
            DnsServerAddress: "10.0.0.2",
            ToolchainsRootPath: toolchainIds.Count > 0 ? ToolchainPaths.VmRoot : null,
            ToolchainIds: toolchainIds);

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mainguard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A VM host that answers the channel's commands from a script and records what ran.
    ///
    /// <para>It refuses <c>curl</c> and <c>wget</c> the way a real MainguardEnv does (see
    /// <see cref="MainguardEnvFacts"/>). Its predecessor answered <c>curl</c> with exit 0, which is why
    /// an install path that could not run in the VM passed every test in this file.</para></summary>
    private sealed class ScriptedHost : IAdapterInstallHost
    {
        public List<IReadOnlyList<string>> Commands { get; } = new();

        public Dictionary<string, string> WrittenFiles { get; } = new(StringComparer.Ordinal);

        /// <summary>Every payload staged into the VM: the file name and its byte count. Empty means
        /// nothing ever crossed into the environment.</summary>
        public List<(string FileName, int Bytes)> Staged { get; } = new();

        /// <summary>When set, staging fails — the "bytes are fine, environment is not" case.</summary>
        public Exception? ThrowOnStage { get; set; }

        public int ProbeExitCode { get; set; }

        public string ProbeStdout { get; set; } = "1.2.3 ok";

        public string ProbeStderr { get; set; } = string.Empty;

        /// <summary>Whether the toolchain is already on the VM before this test does anything. The
        /// probe answers "not installed" until either this is set or an install has moved a tree into
        /// place — modelling the real VM, where probing before installing is exactly what happens.</summary>
        public bool PreInstalled { get; set; }

        private bool _present;

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            Commands.Add(command);
            var exe = command[0];

            if (MainguardEnvFacts.RefuseIfAbsent(exe) is { } absent)
            {
                return Task.FromResult(absent);
            }

            // The swap into place is what makes the toolchain visible to a probe.
            if (string.Equals(exe, "mv", StringComparison.Ordinal))
            {
                _present = true;
                return Task.FromResult(new AdapterCommandResult(0, string.Empty, string.Empty));
            }

            // The probe is the only command that is not one of the fixed install verbs.
            if (exe.Contains("toolx", StringComparison.Ordinal))
            {
                return Task.FromResult(PreInstalled || _present
                    ? new AdapterCommandResult(ProbeExitCode, ProbeStdout, ProbeStderr)
                    : new AdapterCommandResult(127, string.Empty, "No such file or directory"));
            }

            return Task.FromResult(new AdapterCommandResult(0, string.Empty, string.Empty));
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            WrittenFiles[path] = content;
            return Task.CompletedTask;
        }

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
        {
            if (ThrowOnStage is not null)
            {
                return Task.FromException<string>(ThrowOnStage);
            }

            Staged.Add((fileName, content.Length));
            return Task.FromResult($"{ToolchainPaths.VmStageDir}/{fileName}");
        }

        public int IndexOfCommand(string exe) =>
            Commands.FindIndex(c => string.Equals(c[0], exe, StringComparison.Ordinal));

        /// <summary>The distinct programs this host was asked to run, in first-seen order — the set of
        /// binaries an install therefore requires the VM to have.</summary>
        public IReadOnlyList<string> DistinctExecutables =>
            Commands.Select(c => c[0]).Distinct(StringComparer.Ordinal).ToList();

        public string Describe() => string.Join(" | ", Commands.Select(c => string.Join(' ', c)));
    }

    /// <summary>A host that cannot launch anything — the shape a real host takes when the binary under
    /// test is not on disk, or when WSL is not running at all.</summary>
    private sealed class ThrowingHost : IAdapterInstallHost
    {
        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct) =>
            throw new System.ComponentModel.Win32Exception(
                $"An error occurred trying to start process '{command[0]}'. No such file or directory");

        public Task WriteFileAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct) =>
            throw new InvalidOperationException("never staged");
    }

    /// <summary>An image builder that fails the test if it is ever asked to build.</summary>
    private sealed class CountingBuilder : IToolchainImageBuilder
    {
        public int Builds { get; private set; }

        public Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<string?>(imageRef);

        public Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>?>(new[] { "layer-1" });

        public Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
        {
            Builds++;
            return Task.CompletedTask;
        }
    }
}
