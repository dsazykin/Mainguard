using System;
using System.Collections.Generic;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The computed agent-branch-vs-main diff for the review cockpit: the branch name it ran for, the
/// resolved main branch, the raw unified diff, and the parsed <see cref="FilePatch"/> list.</summary>
public sealed record MergeBranchDiff(
    string Branch, string MainBranch, string UnifiedDiff, IReadOnlyList<FilePatch> Files);

/// <summary>The daemon-side bridge P2-47 #7 adds behind <c>MergeQueueService.GetMergeDiff</c>.</summary>
public interface IMergeBranchDiffService
{
    /// <summary>Compute the merge-base diff of an agent's branch against the mirror's main branch.</summary>
    MergeBranchDiff Compute(string repoHash, string agentId);
}

/// <summary>
/// Computes the review cockpit's merge diff (agent branch vs main) by reusing the existing Core git path:
/// the ONE audited git primitive (<c>git diff main...agent/&lt;id&gt;</c> in the daemon's bare mirror, via
/// <see cref="AgentGitCommand"/>) feeding the pure T-06 <see cref="PatchParser"/>. It introduces no new
/// diff algorithm — only the daemon-side bridge that hands the <see cref="ReviewCockpitContext"/> its
/// <c>MergeDiff</c>, which the <c>StreamQueue</c> projection doesn't carry.
///
/// <para>The three-dot range shows exactly what the branch changed since it diverged from main (main's own
/// later commits are excluded) — the right scope for reviewing an agent's work.</para>
/// </summary>
public sealed class MergeBranchDiffService : IMergeBranchDiffService
{
    private readonly IRepoProvisioner _repos;
    private readonly Func<string, string, bool>? _publishAgentRef;

    /// <param name="publishAgentRef">
    /// MG-3 — (repoHash, agentId) → carry the agent's branch from its own repository into the mirror.
    /// Called before the diff is computed: with the agent now committing into its OWN repo, a review
    /// asked for between two ref-watcher ticks would otherwise render an empty diff and read as "the
    /// agent has done nothing". Null (the pre-MG-3 tests) diffs whatever the mirror already holds.
    /// </param>
    public MergeBranchDiffService(IRepoProvisioner repos, Func<string, string, bool>? publishAgentRef = null)
    {
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _publishAgentRef = publishAgentRef;
    }

    public MergeBranchDiff Compute(string repoHash, string agentId)
    {
        if (string.IsNullOrWhiteSpace(repoHash))
        {
            throw new ArgumentException("A repo hash is required.", nameof(repoHash));
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        // MG-3: make the mirror current before reading it (design §7 — the daemon re-fetches immediately
        // before it uses the branch, rather than trusting whatever the watcher last saw).
        _publishAgentRef?.Invoke(repoHash, agentId);

        var barePath = _repos.BareRepoPathFor(repoHash);
        var branch = AgentRepoLayout.BranchFor(agentId);
        var main = ResolveDefaultBranch(barePath);

        // git diff main...agent/<id>: the merge-base diff (what the branch added since it diverged).
        var unified = AgentGitCommand.Run(barePath, "diff", $"{main}...{branch}");
        return new MergeBranchDiff(branch, main, unified, PatchParser.Parse(unified));
    }

    private static string ResolveDefaultBranch(string barePath)
    {
        if (AgentGitCommand.TryRun(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }
}
