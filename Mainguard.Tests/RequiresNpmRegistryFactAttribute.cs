using System;
using System.Net.Http;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips visibly</b> when <c>registry.npmjs.org</c> is not
/// reachable, for the one MG-9 test that deliberately talks to the real registry.
///
/// <para>Why an attribute and not an early <c>return</c>: an early return reports a green
/// <b>"Passed"</b> while asserting nothing, so an offline box would look identical to a verified one —
/// on a test whose entire purpose is to notice when the pinned npm key or the signed message format
/// drifts. A skip is honest; nobody mistakes it for coverage. (This project pins xunit v2, where
/// <c>Assert.Skip</c> reports as a FAILURE, so dynamic skipping is not an option either.)</para>
///
/// <para>The probe is a single short HEAD-equivalent request with a hard timeout, evaluated once per
/// test-class construction, so an offline run costs a second rather than a hang.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresNpmRegistryFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Reachable = new(Probe);

    public RequiresNpmRegistryFactAttribute()
    {
        if (!Reachable.Value)
        {
            Skip =
                "registry.npmjs.org is not reachable — this is the one MG-9 test that verifies npm's "
                + "REAL signature against the key compiled into NpmSigningKeys. Everything else about "
                + "the provenance policy is proved offline in NpmProvenanceTests.";
        }
    }

    private static bool Probe()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("MAINGUARD_SKIP_NETWORK_TESTS"), "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://registry.npmjs.org/-/npm/v1/keys");
            using var response = http.Send(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
