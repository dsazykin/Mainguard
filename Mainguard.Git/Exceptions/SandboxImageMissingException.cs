using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Git.Exceptions;

/// <summary>One image the spawn preflight rejected, and why: absent from the store, or present but
/// version-skewed (its <c>mainguard.image.version</c> label ≠ the daemon's expected constant).</summary>
/// <param name="ImageRef">The image tag/ref (e.g. <c>mainguard-agent-base:latest</c>).</param>
/// <param name="Stale">True when the image is present but outdated; false when it is missing.</param>
public sealed record SandboxImagePreflightProblem(string ImageRef, bool Stale);

/// <summary>
/// The spawn preflight found a required jail image absent — OR present but version-skewed (field
/// failure 2026-07-17, twice: a fresh <c>MainguardEnv</c> import AND the tier-2 VM upgrade both leave
/// the store empty — it lives outside <c>/home/mainguard</c>, so the user-data migration correctly
/// skips it — and a Dockerfile change can leave an old, wrong-bytes image behind under the same tag).
/// Thrown BEFORE any worktree/jail is made, naming exactly which image(s) need attention and the
/// repair, so the failure is one actionable <c>FailedPrecondition</c> regardless of whether the
/// agent-base or the egress-proxy image is the culprit (the latter previously surfaced as an opaque
/// create failure inside the egress setup).
///
/// <para><b>What this message must not do (field failure 2026-08-05).</b> It previously told the user
/// to "restart Mainguard and wait for the 'Sandbox images installed' notice", and offered a manual
/// <c>docker build</c> fallback. Every clause of that was wrong in a way that cost the user an hour:
/// restarting ENDED the in-flight build (the exit path terminated the distro out from under it — see
/// <c>AppShutdownSequence</c> — which is what kept the images stale across every attempt);
/// a stale image's notice reads "updated", never "installed", so the notice named here could not
/// appear; the fallback's <c>&lt;Mainguard dir&gt;/payload/images/</c> path does not exist inside the
/// distro (the sources live on the Windows side, reached via <c>/mnt/…</c>); and the command omitted
/// <c>--label mainguard.image.version=&lt;hash&gt;</c>, so even run against the right path it produced an
/// unlabelled image that the very next probe re-rejects as stale. A manual command that cannot
/// succeed is worse than no manual command, so there is none: this message now names only what was
/// actually checked and the repairs that actually run.</para>
/// </summary>
public class SandboxImageMissingException : MainguardException
{
    /// <summary>All-missing convenience ctor (the presence-only callers/tests).</summary>
    public SandboxImageMissingException(IReadOnlyCollection<string> missingImageTags)
        : this(missingImageTags.Select(t => new SandboxImagePreflightProblem(t, Stale: false)).ToList())
    {
    }

    /// <summary>The reason-tagged ctor — each problem is either missing or outdated.</summary>
    public SandboxImageMissingException(IReadOnlyCollection<SandboxImagePreflightProblem> problems)
        : base(ComposeMessage(problems))
    {
    }

    private static string ComposeMessage(IReadOnlyCollection<SandboxImagePreflightProblem> problems)
    {
        var missing = problems.Where(p => !p.Stale).Select(p => p.ImageRef).ToArray();
        var outdated = problems.Where(p => p.Stale).Select(p => p.ImageRef).ToArray();

        var parts = new List<string>();
        if (missing.Length > 0)
        {
            parts.Add($"missing: {string.Join(", ", missing)}");
        }

        if (outdated.Length > 0)
        {
            parts.Add($"outdated: {string.Join(", ", outdated)}");
        }

        return $"Mainguard OS sandbox image(s) need provisioning ({string.Join("; ", parts)}). Mainguard "
            + "builds these inside Mainguard OS automatically — it starts after launch and takes several "
            + "minutes per image. Leave Mainguard running until the 'Sandbox images installed/updated' "
            + "notice appears — an interrupted build starts over from the beginning. You can also start "
            + "it from Tools → Rebuild sandbox images. If it keeps failing, the per-step build output and "
            + "the docker error are in oobe.log.";
    }
}
