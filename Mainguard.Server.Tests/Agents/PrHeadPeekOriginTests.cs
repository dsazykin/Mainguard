using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// W1-A rework — the external-PR intake's "has the head moved?" peek must not be answered from inside a
/// repository the agent controls.
///
/// <para><see cref="PrHeadFetcher.PeekRemoteHeadAsync"/> runs <c>git ls-remote</c>, and <c>ls-remote</c>
/// needs no repository at all — but it used to run with the WORKER'S WORKTREE as its working directory.
/// On an external pull request that worktree holds a third party's code and its repository config is
/// writable from their jail, so a single <c>url.&lt;decoy&gt;.insteadOf &lt;real&gt;</c> silently
/// redirects the query git actually makes. The peek would then keep answering "the same SHA as last
/// time" and the intake would never fetch again — no re-verification, no invalidation, for as long as
/// the subscription lives.</para>
///
/// <para>The test proves itself non-vacuous first: the same <c>ls-remote</c>, asked from the worktree,
/// comes back with the decoy's SHA.</para>
/// </summary>
public sealed class PrHeadPeekOriginTests : IDisposable
{
    private readonly string _root = AgentTestGit.NewVmRoot();

    public void Dispose() => AgentTestGit.DeleteTree(_root);

    [Fact]
    public async Task Peek_ReadsTheRealRemote_EvenWhenTheWorkersConfigRewritesTheUrl()
    {
        const int pr = 7;
        var real = SeedRemote("real", "the real pull request head", pr);
        var decoy = SeedRemote("decoy", "an answer the agent chose", pr);
        Assert.NotEqual(real.Sha, decoy.Sha);

        var mirror = Path.Combine(_root, "mirror.git");
        AgentTestGit.RunChecked(_root, "clone", "--bare", "-q", real.Path, mirror);

        // The worker's worktree — agent-writable, and pointed somewhere else.
        var worktreeParent = Path.Combine(_root, "worktrees", "hash0");
        Directory.CreateDirectory(worktreeParent);
        var worktree = Path.Combine(worktreeParent, "pr-7");
        Directory.CreateDirectory(worktree);
        AgentTestGit.RunChecked(worktree, "init", "-q");
        AgentTestGit.RunChecked(worktree, "config", "url." + decoy.Path + ".insteadOf", real.Path);

        // Non-vacuous: asked from the worktree, git contacts the decoy.
        var fromWorktree = AgentTestGit.RunChecked(worktree, "ls-remote", real.Path, $"refs/pull/{pr}/head");
        Assert.StartsWith(decoy.Sha, fromWorktree, StringComparison.Ordinal);

        var fetcher = new PrHeadFetcher(
            (_, _) => worktree,
            hostUrl: _ => real.Path,
            resolveMirrorPath: _ => mirror);

        var peeked = await fetcher.PeekRemoteHeadAsync(Source, "hash0", "pr-7", pr, CancellationToken.None);

        Assert.Equal(real.Sha, peeked);
    }

    /// <summary>Without a mirror resolver (the shape a test double or an un-provisioned repo produces)
    /// the peek still refuses to run inside the worktree: it falls back to the worktree's daemon-owned
    /// PARENT, which is no repository at all — which is fine, because <c>ls-remote</c> needs none.</summary>
    [Fact]
    public async Task Peek_WithNoMirrorResolver_StillDoesNotRunInsideTheWorktree()
    {
        const int pr = 9;
        var real = SeedRemote("real", "the real pull request head", pr);
        var decoy = SeedRemote("decoy", "an answer the agent chose", pr);

        var worktreeParent = Path.Combine(_root, "worktrees", "hash0");
        Directory.CreateDirectory(worktreeParent);
        var worktree = Path.Combine(worktreeParent, "pr-9");
        Directory.CreateDirectory(worktree);
        AgentTestGit.RunChecked(worktree, "init", "-q");
        AgentTestGit.RunChecked(worktree, "config", "url." + decoy.Path + ".insteadOf", real.Path);

        var fetcher = new PrHeadFetcher((_, _) => worktree, hostUrl: _ => real.Path);

        Assert.Equal(
            real.Sha,
            await fetcher.PeekRemoteHeadAsync(Source, "hash0", "pr-9", pr, CancellationToken.None));
    }

    private static ExternalPrSource Source => new("github.com", "acme", "fixture", "codex[bot]");

    /// <summary>A repository advertising <c>refs/pull/&lt;n&gt;/head</c>, the ref the production peek asks
    /// a GitHub host for.</summary>
    private (string Path, string Sha) SeedRemote(string name, string content, int pr)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        AgentTestGit.RunChecked(path, "init", "-q");
        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), content + "\n");
        AgentTestGit.RunChecked(path, "add", "-A");
        AgentTestGit.RunChecked(path, "commit", "-q", "-m", content);
        var sha = AgentTestGit.RunChecked(path, "rev-parse", "HEAD").Trim();
        AgentTestGit.RunChecked(path, "update-ref", $"refs/pull/{pr}/head", sha);
        return (path, sha);
    }
}
