using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The RequiresDocker leg of the CLI-login round-trip: login state written inside a real jail survives
/// that jail's teardown and reappears inside a FRESH one.
///
/// <para><b>Why a Docker test and not a fake.</b> Every hop here is a place the round-trip has already
/// been silently broken. The jail's <c>$HOME</c> is a 256 MiB <b>tmpfs</b> mounted over the image's own
/// home, so the file only exists in RAM and dies with the container. The harvest reads it back through
/// a <c>docker exec</c> running as the AGENT uid (the file is 0600, owned by that uid). The restore
/// deliberately goes back in over exec <b>stdin</b> rather than <c>docker cp</c>, because <c>docker
/// cp</c> writes into the image layer UNDERNEATH the tmpfs and reports success — the file is then
/// invisible to everything in the container while every daemon-side signal says it landed. None of
/// those three facts is observable against a fake engine, so a fake would assert the shape of the
/// round-trip while proving nothing about whether it happens.</para>
///
/// <para>The pieces exercised are the production ones: <see cref="SandboxAgentLauncher"/>'s harvest,
/// <see cref="CliLoginVault"/>'s host-keychain format (the client's own persistence), and
/// <c>DockerSandboxEngine</c>'s restore, over jails built by the real <c>ContainerSpecBuilder</c>. The
/// "keychain" is a local string, so nothing here touches a real keyring. Which CALLER drives the
/// harvest in the shipped app is pinned separately by <c>CliLoginHarvestWiringTests</c> — that was the
/// missing half; this is the mechanism it drives.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class CliLoginRoundTripDockerTests
{
    /// <summary>The adapter kind whose marker declares what may be harvested.</summary>
    private const string AgentKind = "probe-cli";

    /// <summary>The one declared <c>credentialPaths</c> entry. Nested, so the restore's
    /// <c>mkdir -p</c> of the parent directory is exercised rather than assumed.</summary>
    private const string LoginPath = ".probe/login.json";

    [RequiresDockerFact]
    public async Task LoginWrittenInAJail_SurvivesTeardown_AndReappearsInAFreshJail()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        // A per-run nonce: neither a leftover container nor a file that happened to already be there
        // can satisfy the final assertion.
        var login = "{\"token\":\"" + Guid.NewGuid().ToString("N") + "\"}";

        using var registry = new TempRegistry(AgentKind, LoginPath);
        var launcher = new SandboxAgentLauncher(
            new EngineOnlyEnvironment(fixture.Engine), new InstalledAdapterCatalog(registry.Path));

        // ---- jail #1: the user signs in inside the terminal --------------------------------------
        var first = await fixture.SpawnAsync(agentId: "login-roundtrip-1", ct: ct);

        var wrote = await fixture.ExecAsync(first.ContainerId,
            "sh", "-c",
            $"mkdir -p \"$(dirname '{JailPath}')\" && printf '%s' '{login}' > '{JailPath}' && chmod 600 '{JailPath}'");
        Assert.True(wrote.ExitCode == 0,
            $"could not simulate the in-jail login: exit={wrote.ExitCode} stderr={wrote.Stderr}");

        // ---- harvest: jail tmpfs → host keychain ---------------------------------------------------
        var harvested = await launcher.HarvestCliCredentialsAsync(first.ContainerId, AgentKind, ct);
        var file = Assert.Single(harvested);
        Assert.Equal(LoginPath, file.HomeRelativePath);
        Assert.Equal(login, Encoding.UTF8.GetString(file.Content));

        // The client's own persistence step, byte-for-byte: this string is what lands in the OS keychain.
        var vault = CliLoginVault.MergeAndSerialize(
            stored: null,
            harvested: new[] { new CliLoginFile(file.HomeRelativePath, file.Content) });
        Assert.NotNull(vault);

        // ---- teardown: the tmpfs home is destroyed with the container -------------------------------
        await fixture.Engine.RemoveAsync(first.ContainerId, ct);

        // ---- jail #2: a fresh jail, restored from the keychain --------------------------------------
        var restored = CliLoginVault.Parse(vault);
        var second = await fixture.SpawnAsync(
            agentId: "login-roundtrip-2", ct: ct,
            cliCredentials: Materialize(restored));

        // Framed rather than substring-matched: a probe that failed to run prints no frame at all, and
        // would otherwise be indistinguishable from a file whose contents merely did not match.
        var read = await fixture.ExecAsync(second.ContainerId,
            "sh", "-c", $"printf 'BEGIN['; cat '{JailPath}' 2>/dev/null; printf ']END'");
        Assert.Equal(0, read.ExitCode);
        Assert.Equal($"BEGIN[{login}]END", read.Stdout.Trim());
    }

    /// <summary>The declared login file's absolute path inside the jail.</summary>
    private static string JailPath => ContainerSpecBuilder.AgentHome + "/" + LoginPath;

    private static IReadOnlyList<SandboxCredentialFile> Materialize(IReadOnlyList<CliLoginFile> files)
    {
        var list = new List<SandboxCredentialFile>(files.Count);
        foreach (var f in files)
        {
            list.Add(new SandboxCredentialFile(f.Path, f.Content));
        }

        return list;
    }

    /// <summary>A temp adapter registry holding ONE install marker — the daemon's only declaration of
    /// which $HOME-relative paths it may harvest from (and restore into) a jail.</summary>
    private sealed class TempRegistry : IDisposable
    {
        public TempRegistry(string agentKind, params string[] credentialPaths)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mg-login-registry-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
            File.WriteAllText(
                System.IO.Path.Combine(Path, agentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    agentKind, "1.0.0", new[] { "/bin/true" },
                    ApiKeyEnvVar: null, EgressHosts: null, CredentialPaths: credentialPaths)));
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* never fail a test from cleanup */ }
        }
    }

    /// <summary>
    /// The substrate slice <see cref="SandboxAgentLauncher.HarvestCliCredentialsAsync"/> actually uses:
    /// the sandbox engine, and nothing else. The launcher's spawn chain is not under test here — the
    /// jails come from <see cref="SandboxFixture"/>, which builds them with the production
    /// <c>ContainerSpecBuilder</c> — so every other member throws rather than quietly answering.
    /// </summary>
    private sealed class EngineOnlyEnvironment : IAgentEnvironment
    {
        public EngineOnlyEnvironment(ISandboxEngine sandboxes) => Sandboxes = sandboxes;

        public string SubstrateId => "docker-test";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public ISandboxEngine Sandboxes { get; }

        public IRepoProvisioner Repos =>
            throw new NotSupportedException("the harvest path never provisions a repo");

        public IAgentWorktreeManager Worktrees =>
            throw new NotSupportedException("the harvest path never touches a worktree");

        public IEgressPolicy Egress =>
            throw new NotSupportedException("the harvest path never touches egress");

        public SyncRemote ResolveSyncRemote(string repoHash) =>
            throw new NotSupportedException("the harvest path never resolves a sync remote");
    }
}
