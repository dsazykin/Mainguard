using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Audit F47 — a pin override may not redirect an install to an arbitrary host.</b>
///
/// <para>The override file's validation was shape-only (HTTPS, 64 hex, a concrete version), and the same
/// file supplies BOTH the URL to fetch and the sha256 that "verifies" what comes back. One line in a
/// user-writable JSON file could therefore point an install at an attacker's server and declare the hash
/// it should match; the hash check passes by construction, because both sides came from the same party.
/// HTTPS bought nothing — the attacker's own server has a valid certificate.</para>
///
/// <para>Both sides of the file are asserted: <see cref="IAdapterPinOverrideStore.Set"/> refuses to write
/// one, and the read path DROPS one it finds. Read-side is the load-bearing half — "we validated it when
/// we wrote it" is not a property of a file anyone can edit.</para>
/// </summary>
public class AdapterPinOverrideHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mg-pin-hosts-" + Guid.NewGuid().ToString("N"));

    private string PinFile => Path.Combine(_dir, "pin-overrides.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("https://registry.npmjs.org/tool/-/tool-1.0.0.tgz", true)]
    [InlineData("https://REGISTRY.NPMJS.ORG/tool/-/tool-1.0.0.tgz", true)]   // host compare is case-insensitive
    [InlineData("https://evil.example.com/tool/-/tool-1.0.0.tgz", false)]
    [InlineData("https://registry.npmjs.org.evil.example.com/t.tgz", false)] // the suffix near-miss
    [InlineData("https://npmjs.org/tool/-/tool-1.0.0.tgz", false)]           // a sibling host is not the host
    [InlineData("http://registry.npmjs.org/tool/-/tool-1.0.0.tgz", false)]   // right host, wrong scheme
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void OnlyTheChannelsOwnHostIsAllowed(string? payloadUrl, bool allowed)
        => Assert.Equal(allowed, AdapterPinHosts.IsAllowed(payloadUrl));

    [Fact]
    public void Set_RefusesAnOverridePointingAtAnotherHost()
    {
        var store = new FileAdapterPinOverrideStore(PinFile);

        var ex = Assert.Throws<ArgumentException>(() => store.Set("tool",
            new AdapterPinOverride("2.0.0", "https://evil.example.com/tool-2.0.0.tgz", Sha)));

        Assert.Contains("not an allowed adapter payload host", ex.Message);
        Assert.False(File.Exists(PinFile)); // nothing was written at all
    }

    [Fact]
    public void Read_DropsAHandEditedOverridePointingAtAnotherHost()
    {
        // The threat model: the attacker does not go through Set. They edit the file — which is plain
        // user-writable JSON in %LocalAppData% — and wait for the next install.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PinFile, JsonSerializer.Serialize(new
        {
            good = new
            {
                version = "1.0.0",
                payloadUrl = "https://registry.npmjs.org/tool/-/tool-1.0.0.tgz",
                sha256 = Sha,
            },
            evil = new
            {
                version = "9.9.9",
                payloadUrl = "https://evil.example.com/tool-9.9.9.tgz",
                sha256 = Sha,
            },
        }));

        var store = new FileAdapterPinOverrideStore(PinFile);

        Assert.Null(store.TryGet("evil"));           // dropped → the bundled pin applies, the safe direction
        Assert.NotNull(store.TryGet("good"));        // one bad entry does not brick every other adapter
        Assert.Equal("1.0.0", store.TryGet("good")!.Version);
    }

    [Fact]
    public void ARedirectedOverrideIsNeverInstalled_EvenThoughItsOwnHashMatches()
    {
        // The end-to-end shape of the finding: the hash check CANNOT catch this, because the override
        // supplied the hash. What catches it is that the entry never survives the read.
        Directory.CreateDirectory(_dir);
        var payload = new byte[] { 1, 2, 3 };
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        File.WriteAllText(PinFile, JsonSerializer.Serialize(new
        {
            tool = new
            {
                version = "9.9.9",
                payloadUrl = "https://evil.example.com/tool-9.9.9.tgz",
                sha256 = sha, // a hash of the attacker's own bytes: self-consistent, and worthless
            },
        }));

        Assert.Null(new FileAdapterPinOverrideStore(PinFile).TryGet("tool"));
    }
}

/// <summary>
/// The other half of audit F47: even an override on an ALLOWED host — the shape
/// <see cref="AgentCliUpdateService"/> itself writes — is not installed on the strength of its own
/// sha256. <see cref="AdapterChannel.EnsureAsync(string,System.Threading.CancellationToken)"/> now runs
/// the provenance gate at the rung the MANIFEST declares, on every install an override governs, and not
/// only where the pin is first moved.
/// </summary>
public class AdapterOverrideProvenanceGateTests
{
    [Fact]
    public async Task Ensure_WithAStoredOverride_RunsTheProvenanceGate_AndRefusesOnItsVerdict()
    {
        var f = new PinnedOverrideRig();
        f.Provenance.Verdict = new NpmProvenanceVerdict(
            NpmProvenanceOutcome.Refused, "no registry signature under a pinned key");

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => f.Channel.EnsureAsync("tool"));

        Assert.Equal(AdapterChannelError.ProvenanceRejected, ex.Error);
        Assert.Contains("no registry signature under a pinned key", ex.Message);
        Assert.Null(f.Host.InstalledVersion); // nothing was staged or installed
    }

    [Fact]
    public async Task Ensure_WithAStoredOverride_InstallsWhenTheGatePasses()
    {
        var f = new PinnedOverrideRig();

        await f.Channel.EnsureAsync("tool");

        Assert.Equal("2.0.0", f.Host.InstalledVersion);
        var call = Assert.Single(f.Provenance.Calls);
        // The rung comes from the MANIFEST, never from the override — a user-writable file that could
        // carry the requirement could lower it to 'none'.
        Assert.Equal(AdapterProvenanceLevel.NpmRegistrySignature, call.Level);
        Assert.Equal("2.0.0", call.Version);
    }

    [Fact]
    public async Task Ensure_WithNoOverride_DoesNotGate_BecauseTheManifestPinIsAReviewedConstant()
    {
        // The bundled sha256 is a constant a human put in the repository, so it is not circular and
        // needs no external anchor. Gating it would also make every offline install impossible.
        var f = new PinnedOverrideRig(withOverride: false);

        await f.Channel.EnsureAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion);
        Assert.Empty(f.Provenance.Calls);
    }

    /// <summary>A channel whose store already holds an accepted-update override for <c>tool</c> — the
    /// state every install AFTER an accepted update runs in, which is exactly the state F47 says was
    /// never re-checked.</summary>
    private sealed class PinnedOverrideRig
    {
        internal static readonly byte[] PayloadOld = System.Text.Encoding.UTF8.GetBytes("tool-payload-1.2.3");
        internal static readonly byte[] PayloadNew = System.Text.Encoding.UTF8.GetBytes("tool-payload-2.0.0");

        public readonly AdapterChannelTests.FakeSource Source = new();
        public readonly AdapterChannelTests.FakeInstallHost Host = new();
        public readonly AgentCliUpdateServiceTests.InMemoryPinStore Pins = new();
        public readonly AgentCliUpdateServiceTests.FakeProvenanceGate Provenance = new();
        public readonly AdapterChannel Channel;

        public PinnedOverrideRig(bool withOverride = true)
        {
            var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(PayloadOld)).ToLowerInvariant();
            var manifest = $$"""
            {
              "adapters": [
                {
                  "id": "tool",
                  "displayName": "Tool",
                  "version": "1.2.3",
                  "provenance": "npm-registry-signature",
                  "sha256": "{{sha}}",
                  "payloadUrl": "https://registry.npmjs.org/tool/-/tool-1.2.3.tgz",
                  "installCmd": ["npm", "install", "-g", "--ignore-scripts", "{payload}"],
                  "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "1.2.3" },
                  "launch": ["/opt/mainguard/adapters/bin/tool"]
                }
              ]
            }
            """;

            Source.ManifestToServe = manifest;
            Source.PayloadToServe = withOverride ? PayloadNew : PayloadOld;

            if (withOverride)
            {
                Pins.Set("tool", new AdapterPinOverride("2.0.0",
                    "https://registry.npmjs.org/tool/-/tool-2.0.0.tgz",
                    Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(PayloadNew)).ToLowerInvariant()));
            }

            Channel = new AdapterChannel(
                Source, Host, new AdapterChannelTests.FakeCache(manifest),
                delay: (_, _) => Task.CompletedTask, pins: Pins, provenance: Provenance);
        }
    }
}
