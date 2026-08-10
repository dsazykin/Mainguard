using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A6 structural guarantees for the daemon read-only git proxy: fetch of an allowlisted host+org
/// prefix succeeds and is transparency-logged; a non-allowlisted prefix is refused + audited; and a
/// push (<c>git-receive-pack</c>) has <b>no code path</b> — it hits the structural refusal.
/// </summary>
public class DaemonGitProxyTests
{
    private static DaemonGitProxy Build(
        IAuditLog audit, INetworkTransparencyLog transparency, out List<GitProxyRequest> fetched)
    {
        var captured = new List<GitProxyRequest>();
        fetched = captured;
        return new DaemonGitProxy(
            new[] { new GitProxyPrefix("github.com", "myorg") },
            audit, transparency,
            req => { captured.Add(req); return new GitFetchResult(4096); });
    }

    [Fact]
    public void Fetch_AllowlistedPrefix_Succeeds_AndTransparencyLogged()
    {
        var audit = new InMemoryAuditLog();
        var transparency = new InMemoryNetworkTransparencyLog();
        var proxy = Build(audit, transparency, out var fetched);

        var result = proxy.ForwardService(new GitProxyRequest(
            DaemonGitProxy.GitUploadPack, "github.com", "myorg", "lib", "agent-1"));

        Assert.Equal(4096, result.Bytes);
        Assert.Single(fetched);
        var line = Assert.Single(transparency.Lines);
        Assert.Equal(EgressVerdict.Allowed, line.Verdict);
        Assert.Empty(audit.Read()); // an allowed fetch is not a denial event
    }

    [Fact]
    public void Fetch_NonAllowlistedPrefix_Refused_AndAudited()
    {
        var audit = new InMemoryAuditLog();
        var transparency = new InMemoryNetworkTransparencyLog();
        var proxy = Build(audit, transparency, out var fetched);

        Assert.Throws<GitProxyRefusedException>(() => proxy.ForwardService(new GitProxyRequest(
            DaemonGitProxy.GitUploadPack, "github.com", "attacker", "payload", "agent-1")));

        Assert.Empty(fetched); // never ran the fetch
        var denied = Assert.Single(audit.Read());
        Assert.Equal(DaemonGitProxy.EgressDeniedEvent, denied.Type);
        Assert.Equal(EgressVerdict.Denied, Assert.Single(transparency.Lines).Verdict);
    }

    [Fact]
    public void Push_ReceivePack_Refused_Structurally_AndAudited()
    {
        var audit = new InMemoryAuditLog();
        var transparency = new InMemoryNetworkTransparencyLog();
        var proxy = Build(audit, transparency, out var fetched);

        // A push presents as git-receive-pack; the proxy has no handler for it — it hits the refusal.
        Assert.Throws<GitProxyRefusedException>(() => proxy.ForwardService(new GitProxyRequest(
            "git-receive-pack", "github.com", "myorg", "lib", "agent-1")));

        Assert.Empty(fetched);
        var denied = Assert.Single(audit.Read());
        Assert.Equal(DaemonGitProxy.EgressDeniedEvent, denied.Type);
    }

    [Fact]
    public void DaemonGitProxy_ExposesNoPushOrReceivePackMethod()
    {
        // Structural proof: there is no push/receive-pack method to call — the refusal is not a policy if.
        var methods = typeof(DaemonGitProxy).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(methods, n => n.Contains("push") || n.Contains("receive"));
    }
}
