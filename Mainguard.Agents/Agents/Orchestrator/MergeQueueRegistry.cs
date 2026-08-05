using System;
using System.Collections.Concurrent;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The per-repo merge-queue objects the daemon serves over gRPC (queue + lease store).</summary>
/// <param name="Queue">The repo's live <see cref="MergeQueue"/>.</param>
/// <param name="Leases">The RT-D1 lease store for the repo (shared daemon store, scoped by repo hash).</param>
public sealed record MergeQueueContext(MergeQueue Queue, IMergeLeaseStore Leases)
{
    /// <summary>
    /// The RT-D2 changed-test-command gate this repo's queue ANDs into <c>CanMerge</c>, when one is wired
    /// (MG-11). Held here — rather than only inside the queue's opaque gate list — because the gate is also
    /// the target of the human acknowledgment RPC: a must-acknowledge gate the daemon can evaluate but the
    /// human cannot clear is a permanently unmergeable branch, not a gate.
    /// </summary>
    public ChangedTestCommandGate? ChangedTestCommand { get; init; }

    /// <summary>
    /// The P2-11 flagged-change gate this repo's queue ANDs into <c>CanMerge</c>. Held here for exactly the
    /// same reason as <see cref="ChangedTestCommand"/>: the daemon evaluates it, and the human's
    /// <c>AcknowledgeFlaggedChange</c> RPC has to be able to reach the very store that is blocking the
    /// merge. A gate reachable only from inside the queue's opaque gate list could be evaluated and never
    /// cleared.
    /// </summary>
    public FlaggedChangeGate? FlaggedChanges { get; init; }
}

/// <summary>
/// Resolves the <see cref="MergeQueue"/> serving a given repo handle. The daemon registers one context
/// per active repo (created when the repo's swarm comes up); the gRPC service resolves through here.
/// Empty until a repo is active — an unknown handle resolves to null (the gRPC layer maps that to a
/// typed NOT_FOUND).
/// </summary>
public interface IMergeQueueRegistry
{
    /// <summary>The context for a repo handle, or null when no queue is active for it.</summary>
    MergeQueueContext? Resolve(string repoHandle);
}

/// <summary>A concurrent in-memory <see cref="IMergeQueueRegistry"/>. The daemon lifecycle registers a
/// context when a repo's swarm starts and removes it on teardown.</summary>
public sealed class MergeQueueRegistry : IMergeQueueRegistry
{
    private readonly ConcurrentDictionary<string, MergeQueueContext> _byHandle = new(StringComparer.Ordinal);

    public MergeQueueContext? Resolve(string repoHandle) =>
        _byHandle.TryGetValue(repoHandle, out var ctx) ? ctx : null;

    /// <summary>Registers (or replaces) the context for a repo handle.</summary>
    public void Register(string repoHandle, MergeQueueContext context) => _byHandle[repoHandle] = context;

    /// <summary>Removes the context for a repo handle (teardown).</summary>
    public void Remove(string repoHandle) => _byHandle.TryRemove(repoHandle, out _);
}
