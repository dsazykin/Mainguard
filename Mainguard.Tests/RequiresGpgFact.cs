using System;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips</b> (visibly) when gpg is not available in this
/// environment, instead of the old <c>throw Xunit.Sdk.SkipException.ForSkip(...)</c> pattern used by
/// the tests in <c>GitServiceSigningTests</c>.
///
/// <para><c>SkipException.ForSkip</c> / dynamic skipping is an xUnit v3 feature. This project pins
/// <c>xunit</c> 2.9.3 (v2 core) with <c>xunit.runner.visualstudio</c> 3.1.4 — the v2 core does not
/// interpret the marker, so throwing <c>SkipException</c> reported these tests as <b>Failed</b>, with
/// the raw <c>$XunitDynamicSkip$…</c> sentinel leaking into the failure message, on every machine
/// without a usable gpg. A permanently-red suite trains everyone to ignore red, which is exactly how a
/// real failure gets waved through. Setting <see cref="FactAttribute.Skip"/> in the constructor is
/// honest: the runner reports a genuine Skipped, and nobody mistakes red for nothing. Mirrors
/// <see cref="Terminal.RequiresLibvtermFactAttribute"/>.</para>
///
/// <para>A <see cref="FactAttribute"/> is constructed at discovery time — no test-class instance and
/// no fixture exist yet — so it cannot consult the per-test <c>GpgTestEnvironment</c> instance
/// (<c>GitServiceSigningTests</c> builds a fresh one, with a fresh ephemeral keyring, per test).
/// Instead <see cref="GpgAvailability"/> stands up one throwaway <see cref="GpgTestEnvironment"/> —
/// the exact same locate-binary-then-generate-a-key probe the tests themselves rely on — fixture-free,
/// and caches the resulting <see cref="GpgTestEnvironment.Available"/> for the run rather than paying
/// for a fresh key generation on every gated test.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresGpgFactAttribute : FactAttribute
{
    public RequiresGpgFactAttribute()
    {
        if (!GpgAvailability.IsSupported)
        {
            Skip = GpgAvailability.SkipReason;
        }
    }
}

/// <summary>
/// Fixture-free gpg availability probe for <see cref="RequiresGpgFactAttribute"/>.
///
/// <para>Delegates to <see cref="GpgTestEnvironment"/> — the same class every
/// <c>GitServiceSigningTests</c> instance already constructs — so the discovery-time skip decision
/// checks the identical real condition (a usable gpg binary AND successful passphrase-less
/// ed25519 key generation in a throwaway <c>GNUPGHOME</c>) rather than a weaker "binary on PATH"
/// stand-in. Probed once per test run and cached in a <see cref="Lazy{T}"/>; the probe's own temp
/// keyring is torn down immediately after the check, before any gated test runs.</para>
/// </summary>
internal static class GpgAvailability
{
    private static readonly Lazy<bool> LazyIsSupported = new(Probe);

    internal static bool IsSupported => LazyIsSupported.Value;

    internal const string SkipReason =
        "gpg is not available in this environment (no usable gpg binary, or ephemeral test-key " +
        "generation failed) — install gpg (or Git for Windows, which bundles it) so the commit/tag " +
        "signing tests can actually run.";

    private static bool Probe()
    {
        using var probe = new GpgTestEnvironment();
        return probe.Available;
    }
}
