using System;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// MG-27 — the pure digest algebra behind "pin by <c>@sha256:</c>, verify at spawn".
///
/// <para><b>Why a digest and not the label.</b> Every jail image carries a
/// <see cref="SandboxImageVersions.LabelKey"/> label whose value is a hash of the image's curated
/// build inputs. That label is a fine <i>staleness</i> signal — it tells you the Dockerfile moved on —
/// but it is not an integrity anchor: a docker label is an arbitrary string that whoever builds an
/// image chooses, so anything that can produce an image named <c>mainguard-agent-base:latest</c> can
/// also stamp it with the expected version and pass the check. A content digest is computed by Docker
/// from the image's own config + layers and cannot be chosen.</para>
///
/// <para><b>Why resolving the tag is half the fix.</b> <c>:latest</c> is a mutable pointer. The
/// preflight used to verify the image behind the tag and then create the container from the TAG, so
/// everything verified was about whichever image held the tag at check time and nothing tied it to the
/// image that actually started. The launcher now resolves each ref to its content digest once, checks
/// THAT, and creates from the digest — the verified bytes and the running bytes are the same bytes by
/// construction.</para>
///
/// <para><b>Why the matcher is not string equality.</b> Docker's container LIST response reports a
/// container's image as whatever short form it likes — a 12-hex-character id for a container created
/// from a digest, the tag for one created from a tag — while the create request carries the full
/// <c>sha256:&lt;64 hex&gt;</c>. The "was this container created from the image we are about to use?"
/// comparison drives DESTRUCTIVE recreate paths in both <c>DockerSandboxEngine</c> (a jail) and
/// <c>EgressProxyConfigurator</c> (the shared proxy, whose recreation strands every running jail's
/// egress — see PR #242). A naive <c>!=</c> against a digest ref would therefore report "upgraded" on
/// EVERY call and recreate the world on every spawn. <see cref="SameImage"/> is the one place that
/// comparison is made, and it understands the short form.</para>
/// </summary>
public static class SandboxImageDigest
{
    /// <summary>The only digest algorithm Docker uses for image content ids.</summary>
    public const string Algorithm = "sha256";

    private const string Prefix = Algorithm + ":";

    /// <summary>The length of a hex-encoded SHA-256.</summary>
    private const int HexLength = 64;

    /// <summary>The shortest hex prefix Docker ever prints for an image id (<c>docker ps</c>).</summary>
    private const int MinShortIdLength = 12;

    /// <summary>
    /// True when <paramref name="reference"/> is a bare content digest (<c>sha256:&lt;64 hex&gt;</c>) —
    /// the form the launcher creates containers from, and the form <see cref="Normalize"/> produces.
    /// </summary>
    public static bool IsDigest(string? reference) =>
        reference is not null
        && reference.StartsWith(Prefix, StringComparison.Ordinal)
        && IsHex(reference.AsSpan(Prefix.Length), HexLength);

    /// <summary>
    /// Canonicalises whatever Docker handed back for an image id into <c>sha256:&lt;64 hex&gt;</c>, or
    /// returns <c>null</c> when the value is not a full digest. Accepts both the prefixed form and a
    /// bare 64-character hex id (some API surfaces omit the algorithm).
    /// </summary>
    public static string? Normalize(string? imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return null;
        }

        var value = imageId.Trim();

        // `name@sha256:…` (a registry ref) carries the digest after the '@'.
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            value = value[(at + 1)..];
        }

        if (IsDigest(value))
        {
            return value;
        }

        return IsHex(value.AsSpan(), HexLength) ? Prefix + value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// True when a container reported as running <paramref name="containerImageField"/> was created
    /// from <paramref name="createdFromRef"/>. Ordinal-equal is the common case; the rest of this
    /// method exists for the short-id form Docker's container LIST endpoint returns (see the type
    /// summary — a false negative here recreates a container, and for the shared egress proxy that is
    /// the destructive act PR #242 spent four CI rounds making rare).
    /// </summary>
    public static bool SameImage(string? containerImageField, string? createdFromRef)
    {
        if (string.IsNullOrWhiteSpace(containerImageField) || string.IsNullOrWhiteSpace(createdFromRef))
        {
            return false;
        }

        var reported = containerImageField.Trim();
        var expected = createdFromRef.Trim();
        if (string.Equals(reported, expected, StringComparison.Ordinal))
        {
            return true;
        }

        // Only a digest-vs-id comparison can be abbreviated; two tags that differ really do differ.
        var expectedDigest = Normalize(expected);
        if (expectedDigest is null)
        {
            return false;
        }

        var reportedDigest = Normalize(reported);
        if (reportedDigest is not null)
        {
            return string.Equals(reportedDigest, expectedDigest, StringComparison.Ordinal);
        }

        // A truncated id (>= 12 hex chars, Docker's own short form) prefix-matches the full digest.
        var hex = reported.StartsWith(Prefix, StringComparison.Ordinal) ? reported[Prefix.Length..] : reported;
        return IsHex(hex.AsSpan(), exactLength: null)
            && hex.Length >= MinShortIdLength
            && expectedDigest.AsSpan(Prefix.Length).StartsWith(hex.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static bool IsHex(ReadOnlySpan<char> value, int? exactLength)
    {
        if (value.Length == 0 || (exactLength is { } n && value.Length != n))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
