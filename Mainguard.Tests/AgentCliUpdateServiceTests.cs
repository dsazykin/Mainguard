using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The Mainguard-managed CLI updater: check-against-npm, user-accepted update as a NEW concrete pin
/// (sha256 of the exact accepted bytes, installed through the channel's verified path), and the
/// one-step revert. The pin discipline must survive every flow — no floating version ever reaches
/// an install, and a failed update restores the prior pin instead of wedging it.
/// </summary>
public class AgentCliUpdateServiceTests
{
    private static readonly byte[] PayloadOld = Encoding.UTF8.GetBytes("tool-payload-1.2.3");
    private static readonly byte[] PayloadNew = Encoding.UTF8.GetBytes("tool-payload-2.0.0");
    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // A {payload}-style manifest (the shipped shape): the staged, hash-verified file is what installs,
    // so an override moves version/payload/sha/probe together with no installCmd rewrite.
    private static string ManifestJson() => $$"""
    {
      "adapters": [
        {
          "id": "tool",
          "displayName": "Tool",
          "version": "1.2.3",
          "provenance": "npm-registry-signature",
          "sha256": "{{ShaOf(PayloadOld)}}",
          "installCmd": ["npm", "install", "-g", "--prefix", "/home/mainguard/mainguard/adapters", "{payload}"],
          "configShims": [],
          "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "1.2.3" },
          "payloadUrl": "https://registry.npmjs.org/tool/-/tool-1.2.3.tgz",
          "launch": ["/opt/mainguard/adapters/bin/tool"]
        }
      ]
    }
    """;

    internal sealed class InMemoryPinStore : IAdapterPinOverrideStore
    {
        public readonly Dictionary<string, AdapterPinOverride> Pins = new(StringComparer.Ordinal);
        public AdapterPinOverride? TryGet(string adapterId) => Pins.TryGetValue(adapterId, out var p) ? p : null;
        public void Set(string adapterId, AdapterPinOverride pin) => Pins[adapterId] = pin;
        public void Remove(string adapterId) => Pins.Remove(adapterId);
    }

    /// <summary>Serves the npm registry endpoints the updater touches; anything else is a 404.</summary>
    private sealed class FakeNpmHandler : HttpMessageHandler
    {
        public string LatestVersion = "2.0.0";
        public byte[] Tarball = PayloadNew;
        public bool FailEverything;
        public readonly List<string> Requested = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (FailEverything)
                throw new HttpRequestException("registry unreachable");

            if (url == "https://registry.npmjs.org/tool/latest")
                return Json($$"""{ "version": "{{LatestVersion}}" }""");
            if (url == $"https://registry.npmjs.org/tool/{LatestVersion}")
                return Json($$"""{ "dist": { "tarball": "https://registry.npmjs.org/tool/-/tool-{{LatestVersion}}.tgz" } }""");
            if (url == $"https://registry.npmjs.org/tool/-/tool-{LatestVersion}.tgz")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tarball) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// Stands in for the MG-9 provenance gate. The pinned npm key lives inside
    /// <see cref="NpmProvenanceGate"/> and cannot be swapped from outside — deliberately — so these
    /// flow tests drive the gate's ANSWER rather than forging npm's signature. The gate's own logic is
    /// proved against a real ECDSA key pair in <see cref="NpmProvenanceTests"/>.
    /// </summary>
    internal sealed class FakeProvenanceGate : INpmProvenanceGate
    {
        public NpmProvenanceVerdict Verdict = new(
            NpmProvenanceOutcome.RegistrySignatureVerified, "fake gate: verified");

        public readonly List<(string AdapterId, AdapterProvenanceLevel Level, string Package, string Version, int Bytes)> Calls = new();

        public Task<NpmProvenanceVerdict> EvaluateAsync(
            string adapterId, AdapterProvenanceLevel declared, string package, string version,
            byte[] payload, CancellationToken ct)
        {
            Calls.Add((adapterId, declared, package, version, payload.Length));
            return Task.FromResult(Verdict);
        }
    }

    private sealed class Fixture
    {
        public readonly AdapterChannelTests.FakeSource Source = new() { ManifestToServe = ManifestJson() };
        public readonly AdapterChannelTests.FakeInstallHost Host = new();
        public readonly InMemoryPinStore Pins = new();
        public readonly FakeNpmHandler Npm = new();
        public readonly FakeProvenanceGate Provenance = new();
        public readonly AdapterChannel Channel;
        public readonly AgentCliUpdateService Updater;
        public readonly List<string> Log = new();

        public Fixture()
        {
            Source.PayloadToServe = PayloadOld;
            Channel = new AdapterChannel(Source, Host, new AdapterChannelTests.FakeCache(ManifestJson()),
                delay: (_, _) => Task.CompletedTask, pins: Pins);
            Updater = new AgentCliUpdateService(Channel, Pins, Npm, Log.Add, Provenance);
        }
    }

    [Theory]
    [InlineData("https://registry.npmjs.org/@anthropic-ai/claude-code/-/claude-code-2.1.210.tgz", "@anthropic-ai/claude-code")]
    [InlineData("https://registry.npmjs.org/opencode-ai/-/opencode-ai-0.1.0.tgz", "opencode-ai")]
    [InlineData("https://example.com/tool/-/tool-1.0.0.tgz", null)] // not npmjs → no update channel
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void TryParseNpmPackage_ReadsTheRegistryTarballShape(string? payloadUrl, string? expected)
        => Assert.Equal(expected, AgentCliUpdateService.TryParseNpmPackage(payloadUrl));

    [Fact]
    public async Task Check_ReportsAnUpgrade_AndStaysQuietWhenCurrent()
    {
        var f = new Fixture();

        var updates = await f.Updater.CheckForUpdatesAsync();
        var update = Assert.Single(updates);
        Assert.Equal(("tool", "1.2.3", "2.0.0"), (update.Id, update.InstalledVersion, update.LatestVersion));

        f.Npm.LatestVersion = "1.2.3"; // registry now agrees with the pin
        Assert.Empty(await f.Updater.CheckForUpdatesAsync());
    }

    [Fact]
    public async Task Check_RegistryDown_IsQuietlyEmpty_NeverAThrow()
    {
        var f = new Fixture();
        f.Npm.FailEverything = true;
        Assert.Empty(await f.Updater.CheckForUpdatesAsync()); // launch-time sweeps must be harmless
    }

    [Fact]
    public async Task EnsureLatest_InstallsTheRegistrysCurrentRelease_NotTheBundledPin()
    {
        // "There is no fixed default install version": a fresh install resolves npm's current
        // release, pins its exact bytes, and installs that — the bundled 1.2.3 is only a fallback.
        var f = new Fixture();
        f.Source.PayloadToServe = PayloadNew;

        await f.Updater.EnsureLatestAsync("tool");

        Assert.Equal("2.0.0", f.Host.InstalledVersion);
        Assert.Equal(ShaOf(PayloadNew), f.Pins.TryGet("tool")!.Sha256);
    }

    [Fact]
    public async Task EnsureLatest_RegistryDown_FallsBackToTheBundledPin()
    {
        var f = new Fixture();
        f.Npm.FailEverything = true;

        await f.Updater.EnsureLatestAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion); // offline → the shipped pin still installs
        Assert.Null(f.Pins.TryGet("tool"));
    }

    [Fact]
    public async Task ApplyUpdate_PinsTheExactNewBytes_InstallsThem_AndKeepsTheOldPinForRevert()
    {
        var f = new Fixture();
        f.Source.PayloadToServe = PayloadNew; // the channel fetch must serve what the pin now covers

        await f.Updater.ApplyUpdateAsync("tool", "2.0.0");

        var pin = f.Pins.TryGet("tool");
        Assert.NotNull(pin);
        Assert.Equal("2.0.0", pin!.Version);
        Assert.Equal(ShaOf(PayloadNew), pin.Sha256); // the sha of the exact accepted bytes
        Assert.Equal("1.2.3", pin.Previous!.Version);
        Assert.Equal("2.0.0", f.Host.InstalledVersion); // installed through the verified channel path
        Assert.Equal("1.2.3", f.Updater.PreviousVersion("tool")); // the settings "Revert" target
    }

    [Fact]
    public async Task ApplyUpdate_TamperedDownload_HashMismatch_RestoresThePriorPin()
    {
        var f = new Fixture();
        // The channel's second fetch serves DIFFERENT bytes than the ones the new pin covered —
        // the install must refuse (hash mismatch) and the override must roll back.
        f.Source.PayloadToServe = Encoding.UTF8.GetBytes("tampered");

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(
            () => f.Updater.ApplyUpdateAsync("tool", "2.0.0"));

        Assert.Equal(AdapterChannelError.HashMismatch, ex.Error);
        Assert.Null(f.Pins.TryGet("tool")); // no override left behind
    }

    [Fact]
    public async Task Revert_RestoresThePreviousPin_AndClearsTheOverrideWhenItWasTheBundledOne()
    {
        var f = new Fixture();
        f.Source.PayloadToServe = PayloadNew;
        await f.Updater.ApplyUpdateAsync("tool", "2.0.0");
        Assert.Equal("1.2.3", f.Updater.PreviousVersion("tool"));

        f.Source.PayloadToServe = PayloadOld; // the bundled pin's bytes
        await f.Updater.RevertAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion);
        Assert.Null(f.Pins.TryGet("tool")); // back to the shipped truth — no override needed
        Assert.Null(f.Updater.PreviousVersion("tool"));
    }

    [Fact]
    public async Task Revert_WithNothingToRevertTo_IsATypedRefusal()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Updater.RevertAsync("tool"));
    }

    // ---- MG-14: an update must move FORWARD, never merely differ ---------------------------------

    [Theory]
    // registry "latest", whether it should be offered as an update to the pinned 1.2.3
    [InlineData("2.0.0", true)]      // newer → a real upgrade
    [InlineData("1.2.4", true)]
    [InlineData("1.10.0", true)]     // numeric ordering: 1.10 > 1.2, though "1.10.0" < "1.2.3" as text
    [InlineData("1.2.3", false)]     // equal → nothing to offer
    [InlineData("1.2.2", false)]     // OLDER → a downgrade dressed as an update
    [InlineData("1.0.0", false)]
    [InlineData("0.9.9", false)]
    [InlineData("1.2.3-rc.1", false)] // a prerelease of the pinned version is BEHIND it
    [InlineData("1.2.3+rebuild", false)] // build metadata alone is not a new version
    public async Task Check_OnlyOffersUpdatesThatMoveForward(string registryLatest, bool offered)
    {
        // The defect: this used to be `!string.Equals(latest, pinned)`, so ANY difference — including
        // a registry that moved its `latest` tag backwards — was presented to the user as an update.
        var f = new Fixture();
        f.Npm.LatestVersion = registryLatest;

        var updates = await f.Updater.CheckForUpdatesAsync();

        Assert.Equal(offered, updates.Count == 1);
        if (offered)
            Assert.Equal(registryLatest, updates[0].LatestVersion);
    }

    [Fact]
    public async Task Check_ARegistryMovingLatestBackwards_IsRefusedAndRecorded_NotSilentlyIgnored()
    {
        var f = new Fixture();
        f.Npm.LatestVersion = "1.0.0"; // upstream un-published 1.2.3, or someone is standing in for it

        Assert.Empty(await f.Updater.CheckForUpdatesAsync());

        // A refusal that only manifests as "no updates" is indistinguishable from "already current".
        var refusal = Assert.Single(f.Updater.RefusedUpdates);
        Assert.Contains("DOWNGRADE", refusal);
        Assert.Contains("1.0.0", refusal);
    }

    [Theory]
    [InlineData("1.0.0", "OLDER")]        // an explicit downgrade request
    [InlineData("1.2.3", "already")]      // re-applying the pinned version is a no-op, not an install
    [InlineData("garbage", "cannot be ordered")]
    public async Task ApplyUpdate_RefusesAnythingThatDoesNotMoveForward_AndLeavesThePinAlone(
        string version, string expected)
    {
        var f = new Fixture();
        f.Source.PayloadToServe = PayloadNew;

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(
            () => f.Updater.ApplyUpdateAsync("tool", version));

        Assert.Equal(AdapterChannelError.UpdateRefused, ex.Error);
        Assert.Contains(expected, ex.Message);
        Assert.Null(f.Pins.TryGet("tool"));            // the pin never moved…
        Assert.Null(f.Host.InstalledVersion);           // …and nothing was installed
        Assert.Contains(f.Updater.RefusedUpdates, r => r.Contains(expected));
    }

    [Fact]
    public async Task EnsureLatest_RegistryLatestIsOlderThanThePin_InstallsThePin_AndSaysSo()
    {
        // The install-side twin: "latest" is the registry's word for it. A registry that moved its tag
        // backwards must not be able to drag a FRESH install backwards with it — the bundled pin is a
        // floor, not merely an offline fallback. Refusing outright would be worse than useless here:
        // it would leave the user with no CLI at all over an upstream mistake.
        var f = new Fixture();
        f.Npm.LatestVersion = "1.0.0";
        f.Source.PayloadToServe = PayloadOld; // the bundled 1.2.3 bytes

        await f.Updater.EnsureLatestAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion);   // the pinned version, not the registry's older one
        Assert.Null(f.Pins.TryGet("tool"));                // no override was written
        Assert.Contains(f.Updater.RefusedUpdates, r => r.Contains("DOWNGRADING"));
    }

    [Fact]
    public async Task EnsureLatest_RegistryAgreesWithThePin_IsAPlainPinnedInstall()
    {
        var f = new Fixture();
        f.Npm.LatestVersion = "1.2.3";
        f.Source.PayloadToServe = PayloadOld;

        await f.Updater.EnsureLatestAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion);
        Assert.Empty(f.Updater.RefusedUpdates); // "already current" is not a refusal
    }

    [Fact]
    public async Task Revert_StillGoesBackwards_TheOneLegitimateBackwardMove()
    {
        // The monotonic guard must not break the escape hatch: Revert writes the previous pin directly
        // rather than going through ApplyUpdateAsync, and that separation is load-bearing.
        var f = new Fixture();
        f.Source.PayloadToServe = PayloadNew;
        await f.Updater.ApplyUpdateAsync("tool", "2.0.0");
        Assert.Equal("2.0.0", f.Host.InstalledVersion);

        f.Source.PayloadToServe = PayloadOld;
        await f.Updater.RevertAsync("tool");

        Assert.Equal("1.2.3", f.Host.InstalledVersion); // deliberately backwards, and it worked
    }

    [Fact]
    public async Task Ensure_HonorsThePinOverride_SoRefreshNeverDowngradesAnUpdatedCli()
    {
        var f = new Fixture();
        f.Pins.Set("tool", new AdapterPinOverride("2.0.0",
            "https://registry.npmjs.org/tool/-/tool-2.0.0.tgz", ShaOf(PayloadNew)));
        f.Source.PayloadToServe = PayloadNew;

        await f.Channel.EnsureAsync("tool");

        Assert.Equal("2.0.0", f.Host.InstalledVersion); // the override governs, not the bundled 1.2.3
    }
}
