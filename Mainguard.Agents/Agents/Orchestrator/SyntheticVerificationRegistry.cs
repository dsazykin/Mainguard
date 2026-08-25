using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>What the real stale cascade's re-queue does with a seeded entry once main moves.</summary>
public enum SyntheticStaleBehavior
{
    /// <summary>Rest at <c>StaleVerified</c> — indistinguishable from an entry awaiting its FIFO
    /// turn in the cascade, which is a state production exhibits and the state a human seeds in
    /// order to look at.</summary>
    Hold,

    /// <summary>Fall through the cascade's honest no-jail terminus: back to <c>Working</c> carrying
    /// the real "no live sandbox" reason — also a state production exhibits.</summary>
    Cascade,
}

/// <summary>
/// One seeded entry's synthetic verification outcome (docs/design/queue-seeding.md §3). The plan
/// replaces exactly the JAIL half of a verification — the sandboxed run — while the mirror-read half
/// (RT-D2 provenance, gate arming, flagged-change review) still executes for real. The outcome is
/// value-supplied, which P2-10 names the forgery the queue exists to prevent, so the record produced
/// from a plan is REQUIRED to be visibly synthetic: its provenance carries
/// <see cref="SeededProvenanceMarker"/> and its artifact says no run happened.
/// </summary>
public sealed class SyntheticVerificationPlan
{
    /// <summary>Appended to <c>VerificationRecord.ResolvedCommand</c> so the immutable record can
    /// never be mistaken for evidence that a test suite ran.</summary>
    public const string SeededProvenanceMarker = " [seeded — not executed]";

    /// <summary>Upper bound on <see cref="HoldSeconds"/> — bounds the daemon's exposure to a
    /// forgotten hold.</summary>
    public const int MaxHoldSeconds = 600;

    public SyntheticVerificationPlan(bool passed, int holdSeconds = 0,
        SyntheticStaleBehavior staleBehavior = SyntheticStaleBehavior.Hold)
    {
        Passed = passed;
        HoldSeconds = Math.Clamp(holdSeconds, 0, MaxHoldSeconds);
        StaleBehavior = staleBehavior;
    }

    /// <summary>The requested outcome. A false settles the entry to <c>Working</c> with a failed
    /// record through the real settle path, exactly as a genuinely red suite would.</summary>
    public bool Passed { get; }

    /// <summary>How long the synthetic run stays GENUINELY in flight (the entry is
    /// <c>Verifying</c>, <c>IsVerificationInFlight</c> is true, <c>ClearStalledVerification</c>
    /// refuses with "wait"). Clamped to [0, <see cref="MaxHoldSeconds"/>].</summary>
    public int HoldSeconds { get; }

    public SyntheticStaleBehavior StaleBehavior { get; }

    /// <summary>Cancels an in-progress hold. The cancellation surfaces out of the verification
    /// delegate, which the queue's real failure path settles — it never fabricates an outcome.</summary>
    public CancellationTokenSource HoldCancellation { get; } = new();

    /// <summary>
    /// The seeded verification run currently executing for this plan, retained by the seeder so
    /// cleanup can AWAIT it before <c>MergeQueue.Cancel</c> deletes the row. This ordering is
    /// load-bearing (design §8): a hold completing after the row is deleted would re-mint the row,
    /// because <c>GetStateLocked</c> defaults an unknown id to <c>Working</c>.
    /// </summary>
    public Task? InFlight { get; set; }
}

/// <summary>
/// The seam through which the ONLY synthetic input of queue seeding reaches
/// <see cref="MergeQueueProvisioner"/>'s verification path (docs/design/queue-seeding.md §3).
///
/// <para><b>Always wired, empty in production — and that emptiness is the security property.</b>
/// The provisioner consults this registry on every verification, but an id that is not registered
/// takes the real path untouched, and the only writer is the flag-gated
/// <c>QueueSeedingService</c>, which a shipped daemon never maps. Wiring it unconditionally keeps
/// the composition root's exact-set optional-control assertion meaningful (one stated wiring
/// decision, pinned) instead of a conditional registration nothing asserts.</para>
///
/// <para>Registration refuses any id without the <see cref="RequiredIdPrefix"/>: a plan for a real
/// agent's id would silently replace that agent's real verification — the one substitution this
/// whole design exists to make impossible.</para>
/// </summary>
public sealed class SyntheticVerificationRegistry
{
    /// <summary>Every seeded id carries this prefix — enforced here at registration and by the
    /// seeder at id minting, so the clear path's scope ("only seed- ids") is structural.</summary>
    public const string RequiredIdPrefix = "seed-";

    private readonly object _gate = new();
    private readonly Dictionary<(string RepoHash, string AgentId), SyntheticVerificationPlan> _plans = new();

    /// <summary>Registers a plan for a seeded id. Throws for an id without the required prefix.</summary>
    public void Register(string repoHash, string agentId, SyntheticVerificationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(repoHash))
        {
            throw new ArgumentException("repoHash is required.", nameof(repoHash));
        }

        if (agentId is null || !agentId.StartsWith(RequiredIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A synthetic verification plan may only be registered for a '{RequiredIdPrefix}'-prefixed id — "
                + "registering one for a real agent's id would silently replace that agent's real verification.",
                nameof(agentId));
        }

        lock (_gate)
        {
            _plans[(repoHash, agentId)] = plan;
        }
    }

    /// <summary>The plan for a (repo, agent), or null — null means "take the real path".</summary>
    public SyntheticVerificationPlan? TryGet(string repoHash, string agentId)
    {
        lock (_gate)
        {
            return _plans.TryGetValue((repoHash, agentId), out var plan) ? plan : null;
        }
    }

    /// <summary>Drops a plan (the clear path's last step for an id).</summary>
    public void Remove(string repoHash, string agentId)
    {
        lock (_gate)
        {
            _plans.Remove((repoHash, agentId));
        }
    }

    /// <summary>Every registered seeded id for a repo (the clear path's enumeration).</summary>
    public IReadOnlyList<string> IdsFor(string repoHash)
    {
        lock (_gate)
        {
            return _plans.Keys.Where(k => k.RepoHash == repoHash).Select(k => k.AgentId).ToList();
        }
    }

    /// <summary>Every registered (repo, agent) pair — <c>GetSeedingStatus</c>'s enumeration.</summary>
    public IReadOnlyList<(string RepoHash, string AgentId)> All()
    {
        lock (_gate)
        {
            return _plans.Keys.ToList();
        }
    }
}
