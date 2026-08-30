using System;

namespace Mainguard.Git.Models;

/// <summary>
/// One <b>immutable</b> verification record (P2-10 invariant 2). Every run of a project's test command
/// inserts a NEW row keyed to the exact <c>main@sha</c> it ran against; a row is never updated. The
/// pass/fail is the daemon-observed container-runtime exit code (OPS SA-1), never a supervisor frame.
/// <para>
/// RT-D2 provenance: <see cref="ResolvedCommand"/> is the exact command line after config resolution,
/// and <see cref="ConfigHash"/> is the SHA-256 of the config file that defined it. A change in either
/// vs the <c>main</c>-side baseline becomes a must-acknowledge flagged item before a merge is allowed —
/// a branch cannot self-green by rewriting its test to <c>exit 0</c>.
/// </para>
/// </summary>
public class VerificationRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The repo this verification belongs to (P2-06 repo hash).</summary>
    public string RepoHash { get; set; } = string.Empty;

    /// <summary>The agent (branch) that was verified.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The exact <c>main@sha</c> the mirror pointed at when the run started.</summary>
    public string MainSha { get; set; } = string.Empty;

    /// <summary>
    /// The exact <c>refs/heads/agent/&lt;id&gt;</c> tip the run was measured ON — the branch-side half of
    /// the pair a verdict is only true between.
    ///
    /// <para>Nullable and empty-tolerated on purpose: every row written before this column existed has no
    /// value, and "unknown" must stay distinguishable from "unchanged". A freshness comparison declines to
    /// answer on an empty value rather than manufacturing a fresh one — see
    /// <c>VerificationRecord.BranchSha</c>.</para>
    /// </summary>
    public string BranchSha { get; set; } = string.Empty;

    /// <summary>Daemon-observed pass/fail (containerd exit code == 0). Never a supervisor-reported value.</summary>
    public bool Passed { get; set; }

    /// <summary>Filesystem path of the captured full log artifact for this run.</summary>
    public string LogArtifactPath { get; set; } = string.Empty;

    /// <summary>RT-D2: the exact resolved test command line that ran.</summary>
    public string ResolvedCommand { get; set; } = string.Empty;

    /// <summary>RT-D2: SHA-256 of the config file that defined the command (branch-side).</summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>When the run completed (UTC).</summary>
    public DateTime WhenUtc { get; set; }
}
