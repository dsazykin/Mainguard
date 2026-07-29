using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Server.Runtime;

/// <summary>One active repository: the opaque handle the daemon keys everything by, and the
/// daemon-openable path of the user's own repository behind it.</summary>
public sealed record ActiveRepo(string Handle, string RepoPath);

/// <summary>
/// The repos this daemon has provisioned, and where the user's own copy of each one lives.
///
/// <para><b>Why it had to exist.</b> Everything daemon-side is keyed by the repo <i>hash</i>
/// (<c>RepoPathHasher.Hash</c> of the caller's path) and the hash is one-way, so once a repo was
/// provisioned the daemon could no longer say which repository it was. That is fine for the mirror and
/// the worktrees, and fatal for the external-PR intake: the T-23 transport resolves a host, an
/// <c>owner/repo</c> slug and a token <i>from a repo path</i>, and matching a subscribed source to a repo
/// means reading that repo's origin remote. With nothing remembering the path, the intake's per-source
/// target resolver was hardwired to return null and the intake materialized nothing in production at
/// all.</para>
///
/// <para>Memory-only and deliberately so: it is a cache of what has been provisioned in THIS daemon
/// lifetime, re-populated by the client's ordinary re-provision when the app reopens a repository — the
/// same posture the merge-queue registry takes.</para>
/// </summary>
public sealed class ActiveRepoIndex
{
    private readonly ConcurrentDictionary<string, string> _byHandle = new(StringComparer.Ordinal);

    /// <summary>Records (or refreshes) the path behind a handle. Called when a repo is provisioned.</summary>
    public void Record(string repoHandle, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        _byHandle[repoHandle] = repoPath;
    }

    /// <summary>The daemon-openable path behind a handle, or null when the handle is unknown.</summary>
    public string? PathFor(string repoHandle)
        => repoHandle is not null && _byHandle.TryGetValue(repoHandle, out var path) ? path : null;

    /// <summary>Every active repo, ordered by handle so a resolve is deterministic.</summary>
    public IReadOnlyList<ActiveRepo> Snapshot()
        => _byHandle.Select(kv => new ActiveRepo(kv.Key, kv.Value))
            .OrderBy(r => r.Handle, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Forgets a handle (repo teardown).</summary>
    public void Remove(string repoHandle) => _byHandle.TryRemove(repoHandle, out _);
}
