using System.Collections.Generic;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-6: the memory-only credential cache must be scoped to <b>(repo, kind)</b>, not to the bare
/// agent kind. Keyed by kind alone it was shared across every repo and session for the daemon's
/// process lifetime, so a coordinator's shim spawn could pull whatever model key or harvested CLI
/// OAuth files were last cached for that kind — potentially supplied by a <i>different repo</i>.
/// </summary>
public sealed class SessionKeyCacheScopeTests
{
    private const string RepoA = "repo-a-hash";
    private const string RepoB = "repo-b-hash";
    private const string Kind = "claude-code";

    [Fact]
    public void ModelKey_IsNeverVisibleToAnotherRepo()
    {
        var cache = new SessionKeyCache();
        cache.Remember(RepoA, Kind, "KEY-FOR-REPO-A");

        // Same repo + kind still reuses (the whole point of the cache).
        Assert.Equal("KEY-FOR-REPO-A", cache.TryGet(RepoA, Kind));

        // A different repo must NOT inherit it — this is the MG-6 defect.
        Assert.Null(cache.TryGet(RepoB, Kind));
    }

    [Fact]
    public void ModelKey_IsNeverVisibleToAnotherKind_InTheSameRepo()
    {
        var cache = new SessionKeyCache();
        cache.Remember(RepoA, Kind, "KEY-FOR-CLAUDE");

        Assert.Null(cache.TryGet(RepoA, "codex"));
    }

    [Fact]
    public void EachRepoKeepsItsOwnKey_NoLastWriterWins()
    {
        var cache = new SessionKeyCache();
        cache.Remember(RepoA, Kind, "KEY-A");
        cache.Remember(RepoB, Kind, "KEY-B");

        // Keyed by kind alone, the second write would have clobbered the first for BOTH repos.
        Assert.Equal("KEY-A", cache.TryGet(RepoA, Kind));
        Assert.Equal("KEY-B", cache.TryGet(RepoB, Kind));
    }

    [Fact]
    public void CliCredentials_AreNeverVisibleToAnotherRepo()
    {
        var cache = new SessionKeyCache();
        var files = new[] { new SandboxCredentialFile(".claude/creds.json", System.Text.Encoding.UTF8.GetBytes("secret-oauth")) };
        cache.RememberCliCredentials(RepoA, Kind, files);

        Assert.NotNull(cache.TryGetCliCredentials(RepoA, Kind));
        Assert.Null(cache.TryGetCliCredentials(RepoB, Kind));
    }

    [Fact]
    public void ExtraEnv_IsNeverVisibleToAnotherRepo()
    {
        var cache = new SessionKeyCache();
        cache.RememberExtraEnv(RepoA, new Dictionary<string, string> { ["LLM_ENV_FOO"] = "bar" });

        Assert.NotNull(cache.TryGetExtraEnv(RepoA));
        Assert.Null(cache.TryGetExtraEnv(RepoB));
    }

    // A blank repo handle must not collapse into a shared bucket — that would recreate the defect
    // for every repo-less caller at once.
    [Fact]
    public void BlankRepoHandle_NeverFormsASharedBucket()
    {
        var cache = new SessionKeyCache();
        cache.Remember(string.Empty, Kind, "ORPHAN-KEY");
        cache.RememberCliCredentials(string.Empty, Kind, new[] { new SandboxCredentialFile("p", System.Text.Encoding.UTF8.GetBytes("c")) });
        cache.RememberExtraEnv(string.Empty, new Dictionary<string, string> { ["A"] = "b" });

        Assert.Null(cache.TryGet(string.Empty, Kind));
        Assert.Null(cache.TryGet(RepoA, Kind));
        Assert.Null(cache.TryGetCliCredentials(string.Empty, Kind));
        Assert.Null(cache.TryGetExtraEnv(string.Empty));
    }

    [Fact]
    public void Miss_ReturnsNull_RatherThanSubstituting()
    {
        var cache = new SessionKeyCache();
        Assert.Null(cache.TryGet(RepoA, Kind));
        Assert.Null(cache.TryGetCliCredentials(RepoA, Kind));
        Assert.Null(cache.TryGetExtraEnv(RepoA));
    }
}
