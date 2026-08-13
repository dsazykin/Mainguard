using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Toolchains;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// The facts about a real MainguardEnv that every fake VM in this suite must honour.
///
/// <para><b>Why this exists.</b> The toolchain install path shipped fetching its payload with
/// <c>curl</c> INSIDE the VM. There is no <c>curl</c> in the VM and there never was —
/// <c>build/mainguardos/packages.pinned.txt</c> pins neither <c>curl</c> nor <c>wget</c>, and a probe of
/// a live <c>MainguardEnv</c> finds only <c>/usr/bin/python3</c> among the three. Every user install died
/// at <c>curl: command not found</c>, exit 127, and the entire test suite stayed green because each fake
/// host answered <c>curl</c> with exit 0. The tests measured a VM that does not exist.</para>
///
/// <para>So a fake VM refuses these the way the real one does. That turns "the install shells out to a
/// binary the environment does not have" from something a human has to notice into a red test, for this
/// regression and for the next one.</para>
/// </summary>
internal static class MainguardEnvFacts
{
    /// <summary>Programs a live MainguardEnv does NOT have. Verified two ways: they are absent from the
    /// pinned package list, and <c>command -v</c> finds neither on a running distro.</summary>
    public static readonly IReadOnlySet<string> AbsentBinaries =
        new HashSet<string>(StringComparer.Ordinal) { "curl", "wget" };

    /// <summary>The result a real VM gives for <paramref name="argv0"/>, or null when the VM really has
    /// it and the fake should carry on.</summary>
    public static AdapterCommandResult? RefuseIfAbsent(string argv0) =>
        AbsentBinaries.Contains(argv0)
            ? new AdapterCommandResult(127, string.Empty, $"/bin/bash: line 1: {argv0}: command not found")
            : null;
}

/// <summary>
/// A payload source that answers from memory. The bytes are derived from the URL so two entries in one
/// fixture get different payloads, and <see cref="Sha256For"/> is what a manifest fixture pins — which
/// means the channel's real SHA-256 comparison runs for real rather than against a hash a fake VM was
/// told to report.
/// </summary>
internal sealed class FakeToolchainPayloadSource : IToolchainPayloadSource
{
    /// <summary>Every URL this source was asked for, in order.</summary>
    public List<Uri> Fetched { get; } = new();

    /// <summary>When set, the source returns bytes that do NOT match the pin — the corruption /
    /// interception case. This is the ONLY way to produce a hash mismatch now: the hash is computed on
    /// the host over the bytes actually held, so no fake VM can talk the channel into accepting or
    /// rejecting a payload it did not hash itself.</summary>
    public bool Corrupt { get; set; }

    /// <summary>When set, the fetch itself fails (no network, DNS, 404…).</summary>
    public Exception? Throw { get; set; }

    public Task<byte[]> FetchAsync(Uri url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Fetched.Add(url);

        if (Throw is not null)
        {
            return Task.FromException<byte[]>(Throw);
        }

        // The real source refuses a plaintext URL; a fake that did not would let a fixture drift to http.
        Mainguard.Agents.Agents.Adapters.PinnedPayloadTransport.RequireHttps(url);

        return Task.FromResult(Corrupt ? PayloadFor(url.ToString() + "#tampered") : PayloadFor(url.ToString()));
    }

    /// <summary>The bytes this source serves for <paramref name="url"/>.</summary>
    public static byte[] PayloadFor(string url) =>
        Encoding.UTF8.GetBytes("mainguard-test-toolchain-payload\n" + url + "\n");

    /// <summary>The SHA-256 a manifest fixture must pin for <paramref name="url"/>, lowercase hex.</summary>
    public static string Sha256For(string url) =>
        Convert.ToHexString(SHA256.HashData(PayloadFor(url))).ToLowerInvariant();
}
