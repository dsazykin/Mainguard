using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;

namespace Mainguard.Tests;

/// <summary>TI-P2-22 #5/#6: the pinned adapter channel — pin survival vs a breaking upstream, hash
/// verification, and in-VM install at the pinned version with a version-matched health probe.</summary>
public class AdapterChannelTests
{
    private static readonly byte[] PayloadVx = Encoding.UTF8.GetBytes("claude-code-payload-1.2.3");
    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ManifestJson(string version, string sha) => $$"""
    {
      "adapters": [
        {
          "id": "claude-code",
          "displayName": "Claude Code",
          "version": "{{version}}",
          "provenance": "npm-registry-signature",
          "sha256": "{{sha}}",
          "installCmd": ["npm", "install", "-g", "tool@{{version}}"],
          "configShims": [{ "path": "/home/agent/.tool/config", "content": "noninteractive=true" }],
          "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "{{version}}" }
        }
      ]
    }
    """;

    // Shared with AgentCliUpdateServiceTests (the updater rides the same channel machinery).
    internal sealed class FakeSource : IAdapterChannelSource
    {
        public string ManifestToServe = "";
        public byte[] PayloadToServe = Array.Empty<byte>();

        /// <summary>Mirrors <see cref="IAdapterChannelSource.IsAuthoritativeLocal"/> (default false —
        /// the remote-channel posture every pre-existing test exercises).</summary>
        public bool AuthoritativeLocal;
        bool IAdapterChannelSource.IsAuthoritativeLocal => AuthoritativeLocal;

        /// <summary>How many payload fetches fail with a TRANSIENT transport fault before one succeeds —
        /// the host-side half of a cold network (this is how `codex` failed on a real setup).</summary>
        public int TransientFetchFailures;

        /// <summary>Fetch throws something that is NOT a transient transport fault: must not be retried.</summary>
        public Exception? PermanentFetchFailure;

        public int FetchAttempts;

        public Task<string> FetchManifestAsync(CancellationToken ct) => Task.FromResult(ManifestToServe);

        public Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct)
        {
            FetchAttempts++;
            if (PermanentFetchFailure is not null)
                return Task.FromException<byte[]>(PermanentFetchFailure);
            if (TransientFetchFailures > 0)
            {
                TransientFetchFailures--;
                // The exact shape the OOBE surfaced: "An error occurred while sending the request."
                return Task.FromException<byte[]>(new HttpRequestException("An error occurred while sending the request."));
            }

            return Task.FromResult(PayloadToServe);
        }
    }

    internal sealed class FakeCache : IAdapterManifestCache
    {
        private string? _json;
        public FakeCache(string? seed = null) => _json = seed;
        public string? Read() => _json;
        public void Write(string manifestJson) => _json = manifestJson;
    }

    internal sealed class FakeInstallHost : IAdapterInstallHost
    {
        public string? InstalledVersion;
        public bool FailProbeAlways;
        public readonly List<IReadOnlyList<string>> Commands = new();
        public readonly Dictionary<string, string> Shims = new();
        private readonly IReadOnlyList<string> _probe = new[] { "tool", "--version" };

        /// <summary>How many install attempts fail with a TRANSIENT network error before one succeeds —
        /// the cold-VM first-boot race (npm's own retries expire inside one dead network window).</summary>
        public int TransientInstallFailures;

        /// <summary>Install fails for a real, non-network reason: must NOT be retried.</summary>
        public bool FailInstallPermanently;

        /// <summary>Install attempts only (the probes in <see cref="Commands"/> are not installs).</summary>
        public int InstallAttempts;

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.SequenceEqual(_probe))
            {
                return Task.FromResult(InstalledVersion is null || FailProbeAlways
                    ? new AdapterCommandResult(1, "", "not installed")
                    : new AdapterCommandResult(0, $"tool version {InstalledVersion}", ""));
            }

            InstallAttempts++;

            if (FailInstallPermanently)
            {
                // A broken package — no network code anywhere in the output.
                return Task.FromResult(new AdapterCommandResult(
                    1, "", "npm ERR! code ELIFECYCLE\nnpm ERR! postinstall script failed"));
            }

            if (TransientInstallFailures > 0)
            {
                TransientInstallFailures--;
                // Verbatim shape of the real failure that killed all three CLIs on a live setup.
                return Task.FromResult(new AdapterCommandResult(
                    1, "", "npm ERR! code ETIMEDOUT\nnpm ERR! syscall read\nnpm ERR! network read ETIMEDOUT"));
            }
            // Install: install EXACTLY the version the command names — never "latest". Both shapes the
            // manifest allows are honoured: a registry-style "pkg@<version>" token, and a staged
            // payload path "<id>-<version>.payload" (the hash-verified-file install the pin requires).
            var pinned = command.FirstOrDefault(t => t.Contains('@'));
            if (pinned is not null)
            {
                InstalledVersion = pinned[(pinned.LastIndexOf('@') + 1)..];
            }
            else if (command.FirstOrDefault(t => t.Contains("/stage/", StringComparison.Ordinal)) is { } staged)
            {
                var name = staged[(staged.LastIndexOf('/') + 1)..staged.LastIndexOf('.')];
                InstalledVersion = name[(name.LastIndexOf('-') + 1)..];
            }
            else
            {
                InstalledVersion = "0";
            }

            return Task.FromResult(new AdapterCommandResult(0, "", ""));
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Shims[path] = content;
            return Task.CompletedTask;
        }

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
        {
            StagedPayloads[fileName] = content;
            return Task.FromResult($"/home/mainguard/mainguard/adapters/stage/{fileName}");
        }

        public readonly Dictionary<string, byte[]> StagedPayloads = new();
    }

    /// <summary>Records the backoff instead of sleeping, so the retry tests are instant and assert the
    /// actual waits rather than merely that a retry happened.</summary>
    private static Func<TimeSpan, CancellationToken, Task> RecordDelays(List<TimeSpan> sink)
        => (d, _) => { sink.Add(d); return Task.CompletedTask; };

    [Fact]
    public async Task Ensure_ShouldRetryInstall_WhenTheVmNetworkIsNotUpYet()
    {
        // The real regression: on a live setup all three CLIs failed with ETIMEDOUT because the installs
        // run seconds after the VM's first boot, before egress is up. The install itself is fine.
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost { TransientInstallFailures = 2 };
        var delays = new List<TimeSpan>();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest), RecordDelays(delays));

        var result = await channel.EnsureAsync("claude-code");

        Assert.Equal(AdapterEnsureResult.Installed, result);
        Assert.Equal("1.2.3", host.InstalledVersion);       // still the PINNED version, not @latest
        Assert.Equal(3, host.InstallAttempts);              // failed, failed, succeeded
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task Ensure_ShouldRetryPayloadFetch_WhenTheHostNetworkIsNotUpYet()
    {
        // The OTHER half of the same outage: the host pulls the pinned tarball over HTTPS, which fails as
        // an HttpRequestException, not an npm code. Retrying only the in-VM install would leave this
        // broken — which is exactly what happened to `codex` on a live setup.
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx, TransientFetchFailures = 2 };
        var host = new FakeInstallHost();
        var delays = new List<TimeSpan>();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest), RecordDelays(delays));

        var result = await channel.EnsureAsync("claude-code");

        Assert.Equal(AdapterEnsureResult.Installed, result);
        Assert.Equal(3, source.FetchAttempts);              // failed, failed, succeeded
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
        Assert.Equal("1.2.3", host.InstalledVersion);
    }

    [Fact]
    public async Task Ensure_ShouldStillVerifyTheHashPin_AfterAFetchRetry()
    {
        // Retrying must never widen what can be installed: the sha256 pin still gates every byte.
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource
        {
            PayloadToServe = Encoding.UTF8.GetBytes("tampered-payload"), // wrong bytes on the retry
            TransientFetchFailures = 1,
        };
        var channel = new AdapterChannel(source, new FakeInstallHost(), new FakeCache(manifest),
            RecordDelays(new List<TimeSpan>()));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.Equal(AdapterChannelError.HashMismatch, ex.Error);
        Assert.Equal(2, source.FetchAttempts);
    }

    [Fact]
    public async Task Ensure_ShouldNotRetryFetch_OnCallerCancellation()
    {
        // A user cancelling setup must abort immediately, never sit through ~14s of backoff.
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new FakeSource
        {
            PayloadToServe = PayloadVx,
            PermanentFetchFailure = new TaskCanceledException("cancelled"),
        };
        var delays = new List<TimeSpan>();
        var channel = new AdapterChannel(source, new FakeInstallHost(), new FakeCache(manifest), RecordDelays(delays));

        await Assert.ThrowsAsync<TaskCanceledException>(() => channel.EnsureAsync("claude-code", cts.Token));

        Assert.Equal(1, source.FetchAttempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Ensure_ShouldNotRetryFetch_OnAPermanentError()
    {
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource
        {
            PayloadToServe = PayloadVx,
            PermanentFetchFailure = new InvalidOperationException("bad channel config"),
        };
        var delays = new List<TimeSpan>();
        var channel = new AdapterChannel(source, new FakeInstallHost(), new FakeCache(manifest), RecordDelays(delays));

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.EnsureAsync("claude-code"));

        Assert.Equal(1, source.FetchAttempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Ensure_ShouldNotRetry_WhenTheInstallIsGenuinelyBroken()
    {
        // A non-network failure must fail FAST — burning ~14s of backoff on a broken package would make
        // every real failure slower to surface.
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost { FailInstallPermanently = true };
        var delays = new List<TimeSpan>();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest), RecordDelays(delays));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.Equal(AdapterChannelError.InstallFailed, ex.Error);
        Assert.Equal(1, host.InstallAttempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Ensure_ShouldGiveUp_AfterTheRetryBudget_AndStillReportInstallFailed()
    {
        // A network that never comes back must still terminate with the same typed error as before.
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost { TransientInstallFailures = 99 };
        var delays = new List<TimeSpan>();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest), RecordDelays(delays));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.Equal(AdapterChannelError.InstallFailed, ex.Error);
        Assert.Equal(4, host.InstallAttempts);   // 1 initial + 3 retries, then it gives up
        Assert.Equal(3, delays.Count);
    }

    [Theory]
    [InlineData("npm ERR! code ETIMEDOUT", true)]
    [InlineData("npm ERR! network read ETIMEDOUT", true)]
    [InlineData("Error: getaddrinfo EAI_AGAIN registry.npmjs.org", true)]
    [InlineData("read ECONNRESET", true)]
    [InlineData("npm ERR! socket hang up", true)]
    [InlineData("npm ERR! code ELIFECYCLE", false)]
    [InlineData("npm ERR! Unexpected end of JSON input", false)]
    [InlineData("ENOSPC: no space left on device", false)]
    public void IsTransientInstallFailure_ShouldMatchNetworkCodesOnly(string stderr, bool expected)
    {
        var result = new AdapterCommandResult(1, "", stderr);
        Assert.Equal(expected, AdapterChannel.IsTransientInstallFailure(result));
    }

    [Fact]
    public async Task Ensure_ShouldInstallInsideVm_AtPinnedVersion_WithHealthProbe()
    {
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        var result = await channel.EnsureAsync("claude-code");

        Assert.Equal(AdapterEnsureResult.Installed, result);
        Assert.Equal("1.2.3", host.InstalledVersion);
        Assert.Contains(host.Commands, c => c.SequenceEqual(new[] { "npm", "install", "-g", "tool@1.2.3" }));
        Assert.Equal("noninteractive=true", host.Shims["/home/agent/.tool/config"]);

        // Idempotent: already-healthy second run installs nothing.
        Assert.Equal(AdapterEnsureResult.AlreadyHealthy, await channel.EnsureAsync("claude-code"));
    }

    [Fact]
    public async Task Ensure_ShouldSurviveBreakingUpstreamRelease()
    {
        // Pin names 1.2.3; the channel "upstream" is now serving a breaking 2.0.0.
        var pinned = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource
        {
            ManifestToServe = ManifestJson("2.0.0", ShaOf(Encoding.UTF8.GetBytes("breaking-2.0.0"))),
            PayloadToServe = PayloadVx,
        };
        var host = new FakeInstallHost();
        // The cache holds the pinned manifest; EnsureAsync never implicitly refreshes to the breaking one.
        var channel = new AdapterChannel(source, host, new FakeCache(pinned));

        await channel.EnsureAsync("claude-code");

        Assert.Equal("1.2.3", host.InstalledVersion); // NOT 2.0.0 — the pin survived the breaking upstream
        Assert.DoesNotContain(host.Commands, c => c.Any(t => t.Contains("2.0.0")));
    }

    // ---- the dynamic-CLI wiring (agentKind → launch argv) ----

    private static string ManifestWithLaunch(string version, string sha) => $$"""
    {
      "adapters": [
        {
          "id": "claude-code",
          "displayName": "Claude Code",
          "version": "{{version}}",
          "provenance": "npm-registry-signature",
          "sha256": "{{sha}}",
          "payloadUrl": "https://registry.npmjs.org/pkg/-/pkg-{{version}}.tgz",
          "installCmd": ["npm", "install", "-g", "--prefix", "/home/mainguard/mainguard/adapters", "{payload}"],
          "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "{{version}}" },
          "launch": ["/opt/mainguard/adapters/bin/claude"]
        }
      ]
    }
    """;

    [Fact]
    public async Task Ensure_ShouldInstallFromTheStagedVerifiedPayload_NotAReDownload()
    {
        // The {payload} placeholder must expand to the staged file: installing from a registry re-resolve
        // would install bytes the sha256 pin never covered — the pin would be decorative.
        var manifest = ManifestWithLaunch("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        await channel.EnsureAsync("claude-code");

        Assert.Equal(PayloadVx, Assert.Single(host.StagedPayloads).Value);
        var install = host.Commands.First(c => c.Contains("install"));
        // The staged name keeps the payload URL's extension — npm dispatches on it (a neutral name makes
        // npm treat the tarball as a directory: ENOTDIR on <file>/package.json).
        Assert.Contains("/home/mainguard/mainguard/adapters/stage/claude-code-1.2.3.tgz", install);
        Assert.DoesNotContain(install, t => t.Contains("{payload}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_ShouldWriteTheLaunchMarker_OnlyAfterAGreenProbe()
    {
        var manifest = ManifestWithLaunch("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        await channel.EnsureAsync("claude-code");

        // The marker is what maps agentKind → the CLI argv the daemon execs in the jail.
        var marker = InstalledAdapterMarker.TryDeserialize(
            host.Shims["/home/mainguard/mainguard/adapters/registry/claude-code.json"]);
        Assert.NotNull(marker);
        Assert.Equal("claude-code", marker!.Id);
        Assert.Equal("1.2.3", marker.Version);
        Assert.Equal(new[] { "/opt/mainguard/adapters/bin/claude" }, marker.Launch);
    }

    [Fact]
    public async Task Ensure_ProbeFails_ShouldNotWriteALaunchMarker()
    {
        // A marker's existence means "runnable". A CLI whose probe never went green must not be
        // advertised to the daemon as launchable.
        var manifest = ManifestWithLaunch("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost { FailProbeAlways = true };
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.DoesNotContain(host.Shims, s => s.Key.Contains("registry/"));
    }

    [Fact]
    public async Task Ensure_ShouldRefuseOnHashMismatch()
    {
        // Manifest pins a sha that does NOT match the payload the channel serves.
        var manifest = ManifestJson("1.2.3", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));
        Assert.Equal(AdapterChannelError.HashMismatch, ex.Error);
        Assert.Null(host.InstalledVersion); // nothing installed after a hash refusal
    }

    // ---- Audit fix #7: the bundled (local-authoritative) manifest outranks a stale cache ----------

    [Fact]
    public async Task Load_AuthoritativeLocalSource_NewPins_OutrankAnOlderVersionsCache()
    {
        // An app update ships new pins in the embedded starter manifest, but the previous version
        // already cached its manifest. The shipped truth must win — the stale cache shadowed it
        // forever before this fix — and the cache is rewritten to the new truth.
        var oldSha = ShaOf(PayloadVx);
        var cache = new FakeCache(ManifestJson("1.0.0", oldSha));
        var source = new FakeSource { ManifestToServe = ManifestJson("2.0.0", oldSha), AuthoritativeLocal = true };
        var channel = new AdapterChannel(source, new FakeInstallHost(), cache);

        var manifest = await channel.LoadManifestAsync();

        Assert.Equal("2.0.0", manifest.Adapters[0].Version);
        Assert.Contains("2.0.0", cache.Read()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_RemoteSource_CacheStaysFirst_NoImplicitRefresh()
    {
        // The hosted-channel posture is unchanged: refresh stays explicit, so a breaking upstream
        // manifest is never auto-adopted just because the app restarted.
        var sha = ShaOf(PayloadVx);
        var cache = new FakeCache(ManifestJson("1.0.0", sha));
        var source = new FakeSource { ManifestToServe = ManifestJson("9.9.9", sha) }; // default: remote
        var channel = new AdapterChannel(source, new FakeInstallHost(), cache);

        var manifest = await channel.LoadManifestAsync();

        Assert.Equal("1.0.0", manifest.Adapters[0].Version);
    }

    [Fact]
    public void BundledStarterSource_IsAuthoritativeLocal()
    {
        IAdapterChannelSource bundled = new BundledAdapterChannelSource();
        Assert.True(bundled.IsAuthoritativeLocal);
    }

    // ---- launcher-only packages: placing the platform executable a script-free install leaves ----

    private const string VmRoot = "/home/mainguard/mainguard/adapters";

    private static string ManifestWithPlatformBinary(string version, string sha, params string[] sources) => $$"""
    {
      "adapters": [
        {
          "id": "claude-code",
          "displayName": "Claude Code",
          "version": "{{version}}",
          "provenance": "npm-registry-signature",
          "sha256": "{{sha}}",
          "payloadUrl": "https://registry.npmjs.org/pkg/-/pkg-{{version}}.tgz",
          "installCmd": ["npm", "install", "-g", "--ignore-scripts", "--prefix", "/home/mainguard/mainguard/adapters", "{payload}"],
          "platformBinary": {
            "sources": {{System.Text.Json.JsonSerializer.Serialize(sources)}},
            "target": "lib/node_modules/pkg/bin/claude.exe"
          },
          "healthProbe": { "command": ["/home/mainguard/mainguard/adapters/bin/claude", "--version"], "expectedVersionSubstring": "{{version}}" },
          "launch": ["/opt/mainguard/adapters/bin/claude"]
        }
      ]
    }
    """;

    /// <summary>
    /// A VM that behaves the way the real one does for a launcher-only npm package, verified against
    /// MainguardEnv: <c>npm install --ignore-scripts</c> lands the launcher AND the platform
    /// subpackages (optional dependencies are resolved; only the lifecycle hooks are suppressed), but
    /// the launcher's <c>bin/*.exe</c> is left as the vendor's stub — a shell script whose whole body
    /// prints "native binary not installed" and exits 1. The probe follows <c>bin/claude</c> to
    /// whatever that file currently is, so it only reports a version once something has been placed
    /// over the stub.
    /// </summary>
    private sealed class LauncherPackageHost : IAdapterInstallHost
    {
        /// <summary>Verbatim from @anthropic-ai/claude-code 2.1.223's bin/claude.exe.</summary>
        public const string StubStderr =
            "Error: claude native binary not installed.\n\n"
            + "Either postinstall did not run (--ignore-scripts, some pnpm configs)\n"
            + "or the platform-native optional dependency was not downloaded\n(--omit=optional).";

        private readonly string _version;

        /// <summary>Candidate path (prefix-relative) → the version that candidate's binary reports.
        /// A candidate ABSENT from this map is one npm did not install for this os/cpu.</summary>
        private readonly Dictionary<string, string> _available;

        public LauncherPackageHost(string version, Dictionary<string, string> available)
        {
            _version = version;
            _available = available;
        }

        private const string Target = "lib/node_modules/pkg/bin/claude.exe";

        /// <summary>What bin/claude currently resolves to: null before install, "stub" after a
        /// script-free install, otherwise the prefix-relative candidate that was placed over it.</summary>
        public string? Placed { get; private set; }

        public readonly List<IReadOnlyList<string>> Commands = new();

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            Commands.Add(command);

            if (command[0] == $"{VmRoot}/bin/claude")
            {
                if (Placed is null)
                    return Task.FromResult(new AdapterCommandResult(127, "", "no such file"));
                if (Placed == "stub")
                    return Task.FromResult(new AdapterCommandResult(1, "", StubStderr));
                return Task.FromResult(new AdapterCommandResult(0, $"{_available[Placed]} (Claude Code)", ""));
            }

            if (command[0] == "npm")
            {
                Placed = "stub"; // the placeholder the suppressed postinstall would have replaced
                return Task.FromResult(new AdapterCommandResult(0, "", ""));
            }

            if (command[0] is "ln" or "cp")
            {
                var source = command[^2][(VmRoot.Length + 1)..];
                var target = command[^1];
                Assert.Equal($"{VmRoot}/{Target}", target);
                if (!_available.ContainsKey(source))
                {
                    return Task.FromResult(new AdapterCommandResult(
                        1, "", $"{command[0]}: cannot stat '{command[^2]}': No such file or directory"));
                }

                Placed = source;
                return Task.FromResult(new AdapterCommandResult(0, "", ""));
            }

            if (command[0] == "chmod")
                return Task.FromResult(new AdapterCommandResult(0, "", ""));

            throw new InvalidOperationException($"unexpected command: {string.Join(' ', command)}");
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Shims[path] = content;
            return Task.CompletedTask;
        }

        public readonly Dictionary<string, string> Shims = new();

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
            => Task.FromResult($"{VmRoot}/stage/{fileName}");
    }

    [Fact]
    public async Task Ensure_ShouldPlaceThePlatformBinary_WhenAScriptFreeInstallLeavesTheVendorStub()
    {
        // The owner-reported failure, reproduced in MainguardEnv before this fix: the install succeeds,
        // the platform subpackage is on disk, and the probe still exits 1 because bin/claude resolves to
        // the vendor's placeholder. Placing the subpackage's binary over it — the file operation the
        // suppressed postinstall would have done — is what makes the probe green.
        const string source = "lib/node_modules/pkg/node_modules/@anthropic-ai/claude-code-linux-x64/claude";
        var manifest = ManifestWithPlatformBinary("2.1.223", ShaOf(PayloadVx), source);
        var host = new LauncherPackageHost("2.1.223", new() { [source] = "2.1.223" });
        var channel = new AdapterChannel(
            new FakeSource { PayloadToServe = PayloadVx }, host, new FakeCache(manifest));

        var result = await channel.EnsureAsync("claude-code");

        Assert.Equal(AdapterEnsureResult.Installed, result);
        Assert.Equal(source, host.Placed);

        // Placed by hardlink, not copy: these binaries are ~300 MB and both paths sit under one prefix.
        Assert.Contains(host.Commands, c => c.SequenceEqual(
            new[] { "ln", "-f", $"{VmRoot}/{source}", $"{VmRoot}/lib/node_modules/pkg/bin/claude.exe" }));

        // The marker means "runnable", so it must only exist because the probe really went green.
        Assert.Contains(host.Shims, s => s.Key.EndsWith("registry/claude-code.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_ShouldTryPlatformBinaryCandidatesInOrder_AndKeepTheFirstThatRuns()
    {
        // Vendors publish several builds per platform (AVX2 vs baseline, glibc vs musl) and npm installs
        // every variant matching os/cpu. Rather than reimplement each vendor's CPU detection, each
        // candidate is placed and validated by the REAL probe. Here the first is not on disk at all.
        const string missing = "lib/node_modules/pkg/node_modules/opencode-linux-x64/bin/opencode";
        const string present = "lib/node_modules/pkg/node_modules/opencode-linux-x64-baseline/bin/opencode";
        var manifest = ManifestWithPlatformBinary("2.1.223", ShaOf(PayloadVx), missing, present);
        var host = new LauncherPackageHost("2.1.223", new() { [present] = "2.1.223" });
        var channel = new AdapterChannel(
            new FakeSource { PayloadToServe = PayloadVx }, host, new FakeCache(manifest));

        var result = await channel.EnsureAsync("claude-code");

        Assert.Equal(AdapterEnsureResult.Installed, result);
        Assert.Equal(present, host.Placed);
        Assert.Contains(host.Commands, c => c[0] == "ln" && c.Contains($"{VmRoot}/{missing}")); // it really tried
    }

    [Fact]
    public async Task Ensure_ShouldFailAsAnInstall_WhenNoPlatformBinaryCandidateIsOnDisk()
    {
        // Nothing could be placed, so the CLI has no executable at all. That is an INSTALL failure, not
        // a probe failure, and saying so is what tells a reader the subpackage never arrived rather
        // than that the binary ran and misbehaved.
        const string source = "lib/node_modules/pkg/node_modules/@anthropic-ai/claude-code-linux-x64/claude";
        var manifest = ManifestWithPlatformBinary("2.1.223", ShaOf(PayloadVx), source);
        var host = new LauncherPackageHost("2.1.223", new());
        var channel = new AdapterChannel(
            new FakeSource { PayloadToServe = PayloadVx }, host, new FakeCache(manifest));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.Equal(AdapterChannelError.InstallFailed, ex.Error);
        Assert.Contains(source, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("registry/", string.Join("|", host.Shims.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_ProbeFailure_ShouldCarryTheProbesOwnStderr_NotOnlyAnExitCode()
    {
        // "health probe exited 1" names a symptom and no cause; the stub's own words name the bug
        // outright. The UI shows this string verbatim, so the cause has to be IN it.
        var manifest = ManifestWithPlatformBinary("2.1.223", ShaOf(PayloadVx),
            "lib/node_modules/pkg/node_modules/absent/claude");
        var host = new StubOnlyHost();
        var channel = new AdapterChannel(
            new FakeSource { PayloadToServe = PayloadVx }, host, new FakeCache(manifest));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.Equal(AdapterChannelError.ProbeFailed, ex.Error);
        Assert.Contains("native binary not installed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--ignore-scripts", ex.Message, StringComparison.Ordinal);
        // Collapsed to one line: a wrapped "exited\n1." rendered the exit code as a list item.
        Assert.DoesNotContain('\n', ex.Message);
    }

    /// <summary>A VM where the placement SUCCEEDS but the placed binary still refuses to run (a wrong
    /// CPU variant, a truncated download) — so the probe, not the placement, is what reports the cause.</summary>
    private sealed class StubOnlyHost : IAdapterInstallHost
    {
        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
            => Task.FromResult(command[0] == $"{VmRoot}/bin/claude"
                ? new AdapterCommandResult(1, "", LauncherPackageHost.StubStderr)
                : new AdapterCommandResult(0, "", ""));

        public Task WriteFileAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
            => Task.FromResult($"{VmRoot}/stage/{fileName}");
    }

    // ---- MG-9: the --ignore-scripts poison canary ----------------------------------------------

    [Fact]
    public async Task ShippedInstallCommands_RunScriptFree_SoAPoisonedPostinstallNeverExecutes()
    {
        // The behavioural half of the structural guard in AdapterManifestTests, in the idiom
        // ForegroundMergeServiceTests uses for the same hazard on the host side: a fake package manager
        // that "runs" the poisoned lifecycle hook ONLY when --ignore-scripts is absent. Drive the REAL
        // shipped manifest through the real install path and prove the hook never fires.
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
        foreach (var shipped in manifest.Adapters)
        {
            var payload = Encoding.UTF8.GetBytes($"payload-for-{shipped.Id}");
            var single = $$"""
            {
              "adapters": [
                {
                  "id": "{{shipped.Id}}",
                  "displayName": "{{shipped.DisplayName}}",
                  "version": "{{shipped.Version}}",
                  "provenance": "npm-registry-signature",
                  "sha256": "{{ShaOf(payload)}}",
                  "installCmd": {{System.Text.Json.JsonSerializer.Serialize(shipped.InstallCmd)}},
                  "configShims": [],
                  "healthProbe": {
                    "command": {{System.Text.Json.JsonSerializer.Serialize(shipped.HealthProbe!.Command)}},
                    "expectedVersionSubstring": "{{shipped.Version}}"
                  },
                  "payloadUrl": "{{shipped.PayloadUrl}}"
                }
              ]
            }
            """;

            var host = new PoisonedNpmHost(shipped.HealthProbe.Command, shipped.Version);
            var channel = new AdapterChannel(
                new FakeSource { ManifestToServe = single, PayloadToServe = payload },
                host,
                new FakeCache(single),
                delay: (_, _) => Task.CompletedTask);

            await channel.EnsureAsync(shipped.Id);

            Assert.False(host.PostinstallRan,
                $"'{shipped.Id}' installed WITHOUT --ignore-scripts — an upstream postinstall would "
                + "have executed inside MainguardEnv.");
        }
    }

    /// <summary>A fake npm that executes the "postinstall" (sets a flag) unless <c>--ignore-scripts</c>
    /// is on the command line — the canary. The probe answers green so the install path completes.</summary>
    private sealed class PoisonedNpmHost : IAdapterInstallHost
    {
        private readonly IReadOnlyList<string> _probe;
        private readonly string _version;

        public PoisonedNpmHost(IReadOnlyList<string> probe, string version)
        {
            _probe = probe;
            _version = version;
        }

        public bool PostinstallRan { get; private set; }

        public bool Installed { get; private set; }

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            if (command.SequenceEqual(_probe))
            {
                // The FIRST probe must miss, or EnsureAsync short-circuits as AlreadyHealthy and the
                // canary never gets the chance to fire — a vacuous pass.
                return Task.FromResult(Installed
                    ? new AdapterCommandResult(0, $"version {_version}", "")
                    : new AdapterCommandResult(1, "", "not installed"));
            }

            // Only the PACKAGE-MANAGER invocation can run lifecycle scripts. The install path also
            // issues plain file commands (ln/cp/chmod, placing the platform executable a script-free
            // install leaves unplaced), and counting those as installs would have made this canary
            // fire on every adapter that declares a platformBinary — a false red that would most
            // likely have been "fixed" by loosening the canary.
            if (command.Count > 0 && command[0] is "npm" or "pnpm" or "yarn")
            {
                if (!command.Contains("--ignore-scripts"))
                    PostinstallRan = true;

                Installed = true;
            }

            return Task.FromResult(new AdapterCommandResult(0, "", ""));
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
            => Task.FromResult($"/stage/{fileName}");
    }
}
