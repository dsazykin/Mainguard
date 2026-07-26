using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-27 — the digest algebra that turns "pinned by a settable label behind a floating tag" into
/// "pinned by content".
///
/// <para>Two properties are load-bearing and they pull in opposite directions, which is why they are
/// pinned together. <b>Discrimination:</b> a different image must not be mistaken for the pinned one,
/// or the pin is decoration. <b>Non-recreation:</b> the SAME image reported in Docker's abbreviated
/// short-id form must be recognised, because this comparison drives destructive recreate paths — a
/// jail is destroyed and rebuilt, and the shared egress proxy is replaced, which strands every running
/// jail's egress (measured, PR #242). A matcher that answered "different" for the short form would
/// recreate the world on every single spawn.</para>
/// </summary>
public sealed class SandboxImageDigestTests
{
    private const string Digest = "sha256:fd8d9aa63ba2f0982b5304e1ee8d3b90a210bc1ffb5314d980eb6962f1a9715d";
    private const string ShortId = "fd8d9aa63ba2";
    private const string OtherDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void IsDigest_AcceptsAFullContentDigest_AndRejectsTagsAndTruncations()
    {
        Assert.True(SandboxImageDigest.IsDigest(Digest));
        Assert.False(SandboxImageDigest.IsDigest("mainguard-agent-base:latest"));
        Assert.False(SandboxImageDigest.IsDigest("sha256:" + ShortId));   // truncated
        Assert.False(SandboxImageDigest.IsDigest("sha512:" + new string('a', 64)));
        Assert.False(SandboxImageDigest.IsDigest(null));
    }

    [Fact]
    public void Normalize_CanonicalisesEveryFormDockerHandsBack()
    {
        Assert.Equal(Digest, SandboxImageDigest.Normalize(Digest));
        // Some API surfaces omit the algorithm prefix.
        Assert.Equal(Digest, SandboxImageDigest.Normalize(Digest["sha256:".Length..]));
        // A registry ref carries the digest after the '@'.
        Assert.Equal(Digest, SandboxImageDigest.Normalize("mainguard-agent-base@" + Digest));
        Assert.Null(SandboxImageDigest.Normalize("mainguard-agent-base:latest"));
        Assert.Null(SandboxImageDigest.Normalize("   "));
    }

    // The non-recreation half. Docker's container LIST endpoint reports a container created from a
    // digest as a 12-character short id, while the create request carried the full digest. Verified
    // against a real daemon: `docker run sha256:<64 hex>` then `docker ps --format {{.Image}}` prints
    // `fd8d9aa63ba2`. A `!=` comparison here reads that as "the image was upgraded".
    [Fact]
    public void SameImage_RecognisesDockersShortIdFormOfTheSameDigest()
    {
        Assert.True(SandboxImageDigest.SameImage(ShortId, Digest));
        Assert.True(SandboxImageDigest.SameImage("sha256:" + ShortId, Digest));
        Assert.True(SandboxImageDigest.SameImage(Digest, Digest));
    }

    // The discrimination half — a pin that matches everything pins nothing.
    [Fact]
    public void SameImage_RejectsADifferentImage()
    {
        Assert.False(SandboxImageDigest.SameImage(ShortId, OtherDigest));
        Assert.False(SandboxImageDigest.SameImage(OtherDigest, Digest));

        // A container still recorded against the TAG is not the same as one created from the digest:
        // that is precisely the upgrade case, and it must recreate exactly once.
        Assert.False(SandboxImageDigest.SameImage("mainguard-agent-base:latest", Digest));

        // Too short to be a Docker short id — never treat an arbitrary hex fragment as a match.
        Assert.False(SandboxImageDigest.SameImage("fd8d", Digest));
    }

    [Fact]
    public void SameImage_StillComparesTwoRefsExactly_WhenNeitherIsADigest()
    {
        Assert.True(SandboxImageDigest.SameImage("mainguard-agent-base:latest", "mainguard-agent-base:latest"));
        Assert.False(SandboxImageDigest.SameImage("mainguard-agent-base:latest", "mainguard-agent-base:dev"));
        Assert.False(SandboxImageDigest.SameImage(null, Digest));
        Assert.False(SandboxImageDigest.SameImage(Digest, null));
    }
}
