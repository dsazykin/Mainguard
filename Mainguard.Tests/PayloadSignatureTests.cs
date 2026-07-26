using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The signature seam (<see cref="IPayloadSignatureVerifier"/>) and its three enforcement points.
///
/// <para><b>What these tests are NOT.</b> They do not show that anything is verified — nothing is.
/// Mainguard ships no code-signing identity, so the shipped verifier checks nothing and says so. What
/// they pin down is the CONTRACT that makes turning verification on a one-line change: the default
/// never claims to have verified anything, a verifier that says no is obeyed at every privileged
/// promote, and a verifier that faults is treated as a refusal rather than as permission.</para>
/// </summary>
public class PayloadSignatureTests : IDisposable
{
    public void Dispose() => PayloadSignature.ResetToDefault();

    /// <summary>A verifier with a fixed answer, so the enforcement side can be exercised without one
    /// existing. Records what it was asked about, so we can prove the call site reaches it at all.</summary>
    private sealed class ScriptedVerifier : IPayloadSignatureVerifier
    {
        public required Func<SignedArtifactKind, string, SignatureVerdict> Answer { get; init; }
        public List<(SignedArtifactKind Kind, string What)> Asked { get; } = new();

        public SignatureVerdict VerifyFile(SignedArtifactKind kind, string path)
        {
            Asked.Add((kind, path));
            return Answer(kind, path);
        }

        public SignatureVerdict VerifyBytes(SignedArtifactKind kind, string description, byte[] content)
        {
            Asked.Add((kind, description));
            return Answer(kind, description);
        }
    }

    private static void Install(Func<SignedArtifactKind, string, SignatureVerdict> answer)
        => PayloadSignature.Verifier = new ScriptedVerifier { Answer = answer };

    /// <summary>A minimal pinned channel over the shared adapter fakes: one adapter, "tool" @ 1.2.3,
    /// whose payload bytes hash to the pin — so the ONLY thing that can refuse an install here is the
    /// signature gate under test.</summary>
    private sealed class ChannelFixture
    {
        private static readonly byte[] Payload = System.Text.Encoding.UTF8.GetBytes("tool-payload-1.2.3");

        public readonly AdapterChannelTests.FakeSource Source = new();
        public readonly AdapterChannelTests.FakeInstallHost Host = new();
        public readonly AdapterChannel Channel;

        public ChannelFixture()
        {
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Payload)).ToLowerInvariant();
            var manifest = $$"""
            {
              "adapters": [
                {
                  "id": "tool",
                  "displayName": "Tool",
                  "version": "1.2.3",
                  "sha256": "{{sha}}",
                  "installCmd": ["npm", "install", "-g", "--ignore-scripts", "{payload}"],
                  "configShims": [],
                  "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "1.2.3" },
                  "payloadUrl": "https://registry.npmjs.org/tool/-/tool-1.2.3.tgz"
                }
              ]
            }
            """;
            Source.ManifestToServe = manifest;
            Source.PayloadToServe = Payload;
            Channel = new AdapterChannel(
                Source, Host, new AdapterChannelTests.FakeCache(manifest), delay: (_, _) => Task.CompletedTask);
        }
    }

    // ---- the contract ---------------------------------------------------------------------------

    [Theory]
    [InlineData(SignedArtifactKind.DaemonPayload)]
    [InlineData(SignedArtifactKind.AdapterPayload)]
    [InlineData(SignedArtifactKind.ElevatedHelper)]
    [InlineData(SignedArtifactKind.ResumeTarget)]
    public void TheShippedDefault_NeverClaimsToHaveVerifiedAnything(SignedArtifactKind kind)
    {
        // The whole point of a three-valued verdict: "I could not check" must not be spelled the same
        // way as "I checked and it is fine". A default that answered Verified would be a lie that every
        // call site — and every later reader — would then rely on.
        var file = PayloadSignature.VerifyFile(kind, "/some/artifact");
        var bytes = PayloadSignature.VerifyBytes(kind, "some@1.0.0", new byte[] { 1, 2, 3 });

        foreach (var verdict in new[] { file, bytes })
        {
            Assert.Equal(SignatureVerdictKind.NotAvailable, verdict.Kind);
            Assert.NotEqual(SignatureVerdictKind.Verified, verdict.Kind);
            Assert.False(verdict.MustRefuse); // …and it does not break every install either
            Assert.Contains("no code-signing identity", verdict.Reason);
        }
    }

    [Fact]
    public void AVerifierThatFaults_IsARefusal_NotPermission()
    {
        // Fail-closed. Reading an exception as "carry on" is how signature checks get quietly skipped.
        Install((_, _) => throw new InvalidOperationException("trust store unreachable"));

        var verdict = PayloadSignature.VerifyFile(SignedArtifactKind.DaemonPayload, "/x");

        Assert.Equal(SignatureVerdictKind.Rejected, verdict.Kind);
        Assert.True(verdict.MustRefuse);
        Assert.Contains("trust store unreachable", verdict.Reason);
    }

    [Fact]
    public void TheVerifierCannotBeUnset()
        => Assert.Throws<ArgumentNullException>(() => PayloadSignature.Verifier = null!);

    // ---- enforcement point 1: the adapter payload -----------------------------------------------

    [Fact]
    public async Task AdapterInstall_IsRefused_WhenAVerifierRejectsThePayload()
    {
        Install((_, _) => SignatureVerdict.Rejected("not signed by Mainguard"));
        var f = new ChannelFixture();

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => f.Channel.EnsureAsync("tool"));

        Assert.Equal(AdapterChannelError.SignatureRejected, ex.Error);
        Assert.Contains("not signed by Mainguard", ex.Message);
        Assert.Null(f.Host.InstalledVersion); // refused BEFORE anything ran in the VM
    }

    [Fact]
    public async Task AdapterInstall_AsksAboutTheExactPinnedVersion_AndProceedsUnderTheShippedDefault()
    {
        var verifier = new ScriptedVerifier { Answer = (_, _) => SignatureVerdict.NotAvailable("no identity") };
        PayloadSignature.Verifier = verifier;
        var f = new ChannelFixture();

        await f.Channel.EnsureAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion); // NotAvailable must never block a legitimate install
        Assert.Contains(verifier.Asked, a => a.Kind == SignedArtifactKind.AdapterPayload && a.What == "tool@1.2.3");
    }

    // ---- enforcement point 2: the daemon payload promoted to root -------------------------------

    [Fact]
    public async Task DaemonRefresh_IsRefused_WhenAVerifierRejectsThePayload_BeforeTheVmIsTouched()
    {
        Install((_, _) => SignatureVerdict.Rejected("payload signature missing"));
        var payload = Path.Combine(Path.GetTempPath(), "mainguard-sig-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, DaemonPayloadManifest.RequiredAssembly), "bytes");

        var wsl = new SilentWslRunner();
        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("payload signature missing", result.Message);
        // The running daemon was never stopped and nothing was copied or swapped; the one call made is
        // the recovery path's unconditional `systemctl start`, which must always run.
        Assert.Equal(
            new[] { new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "start", "mainguardd" } },
            wsl.Calls);
    }

    // ---- enforcement point 3: the elevated helper -----------------------------------------------

    [Fact]
    public async Task ElevatedLaunch_IsRefused_WhenAVerifierRejectsTheHelper_BeforeAnyUacPrompt()
    {
        Install((_, _) => SignatureVerdict.Rejected("helper is not Authenticode-signed"));

        // Both paths must be inside the running install's directory, so the ONLY thing left to fail is
        // the signature check — which is what makes this test about signatures and not about paths.
        var baseDir = AppContext.BaseDirectory;
        var helper = Path.Combine(baseDir, "mainguard-sigtest-helper.exe");
        var resume = Path.Combine(baseDir, "mainguard-sigtest-resume.exe");
        var resultPath = Path.Combine(Path.GetTempPath(), "mainguard-sig-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(helper, "stub");
        File.WriteAllText(resume, "stub");
        try
        {
            var launcher = new RunAsElevationLauncher(helper, resume, resultPath);

            var ex = await Assert.ThrowsAsync<BootstrapException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));

            Assert.Contains("signature check", ex.Message);
            Assert.Contains("helper is not Authenticode-signed", ex.Message);
        }
        finally
        {
            File.Delete(helper);
            File.Delete(resume);
            try { File.Delete(resultPath); } catch { }
        }
    }

    private sealed class SilentWslRunner : IWslRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(new WslRunResult(0, "", ""));
        }
    }
}
