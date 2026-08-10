using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-07 A6 + F5 suite: drives the real <see cref="DaemonGitProxy"/> with a daemon-side git fetch
/// against a local bare repo standing in for the allowlisted host. Proves an allowlisted-prefix fetch
/// succeeds (transparency-logged), a push (<c>receive-pack</c>) is refused structurally + audited with
/// no ref moved, a non-allowlisted prefix is refused + audited, and an arbitrary VCS dependency is
/// scoped out by the declared-dependency resolver (never silent — F5). Tagged RequiresDocker to run on
/// the full CI leg (needs the git CLI).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class DaemonGitProxyDockerTests : IDisposable
{
    private readonly string _upstream;

    public DaemonGitProxyDockerTests()
    {
        _upstream = Path.Combine(Path.GetTempPath(), "mainguard-a6-upstream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_upstream);
        var work = Path.Combine(Path.GetTempPath(), "mainguard-a6-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Git(_upstream, "init", "--bare");
            Git(work, "init");
            Git(work, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "--allow-empty", "-m", "seed");
            Git(work, "remote", "add", "up", _upstream);
            Git(work, "push", "up", "HEAD:refs/heads/main");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private DaemonGitProxy Build(IAuditLog audit, INetworkTransparencyLog transparency)
    {
        // The daemon-side fetch runner: real git, only ever invoked for an allowlisted prefix.
        GitFetchResult Runner(GitProxyRequest req)
        {
            var output = Git(_upstream, "ls-remote", _upstream);
            return new GitFetchResult(output.Length);
        }

        return new DaemonGitProxy(
            new[] { new GitProxyPrefix("local", "allowed") }, audit, transparency, Runner);
    }

    [RequiresDockerFact]
    public void GitProxy_AllowlistedPrefixFetch_Succeeds()
    {
        var audit = new InMemoryAuditLog();
        var transparency = new InMemoryNetworkTransparencyLog();
        var proxy = Build(audit, transparency);

        var result = proxy.ForwardService(new GitProxyRequest(
            DaemonGitProxy.GitUploadPack, "local", "allowed", "repo", "agent-1"));

        Assert.True(result.Bytes > 0);
        Assert.Contains(transparency.Lines, l => l.Verdict == EgressVerdict.Allowed);
    }

    [RequiresDockerFact]
    public void GitProxy_PushRefusedAndAudited_NoRefMoved()
    {
        var audit = new InMemoryAuditLog();
        var proxy = Build(audit, new InMemoryNetworkTransparencyLog());
        var before = RefState(_upstream);

        Assert.Throws<GitProxyRefusedException>(() => proxy.ForwardService(new GitProxyRequest(
            "git-receive-pack", "local", "allowed", "repo", "agent-1")));

        Assert.Contains(audit.Read(), e => e.Type == DaemonGitProxy.EgressDeniedEvent);
        Assert.Equal(before, RefState(_upstream)); // no ref moved
    }

    [RequiresDockerFact]
    public void GitProxy_NonAllowlistedPrefix_Refused()
    {
        var audit = new InMemoryAuditLog();
        var transparency = new InMemoryNetworkTransparencyLog();
        var proxy = Build(audit, transparency);

        Assert.Throws<GitProxyRefusedException>(() => proxy.ForwardService(new GitProxyRequest(
            DaemonGitProxy.GitUploadPack, "local", "attacker", "payload", "agent-1")));

        Assert.Contains(audit.Read(), e => e.Type == DaemonGitProxy.EgressDeniedEvent);
        Assert.Contains(transparency.Lines, l => l.Verdict == EgressVerdict.Denied);
    }

    [RequiresDockerFact]
    public void PackageProxy_ArbitraryVcsFetch_ShouldBeScopedOrTransparencyLogged()
    {
        // F5: a non-declared VCS dependency is scoped out (typed denial) — never a silent proxy.
        const string goMod = "module x\n\nrequire github.com/spf13/cobra v1.8.0\n";
        var declared = DeclaredDependencyResolver.Resolve(new DeclaredDependencyInputs(GoMod: goMod));

        Assert.True(declared.Allows("github.com/spf13/cobra"));
        Assert.Throws<DeclaredDependencyDeniedException>(() => declared.EnsureAllowed("github.com/attacker/payload"));
    }

    private static string RefState(string repo) => Git(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    private static string Git(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_upstream)) Directory.Delete(_upstream, recursive: true); } catch { }
    }
}
