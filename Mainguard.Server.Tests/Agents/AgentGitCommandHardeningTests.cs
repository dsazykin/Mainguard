using System;
using System.IO;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// W1-A layer 1 — the belt. Every daemon-side git runs, as the daemon user on the host, against a
/// repository the jailed agent can write. These tests plant the audit's exact payload (a repo-local
/// <c>filter.*</c> driver plus a <c>.gitattributes</c> line that assigns it) and measure whether the
/// driver's command is executed.
///
/// <para><b>Each execution test proves itself non-vacuous first.</b> An unhardened <c>git</c> — the
/// test harness's own runner, which is what the daemon effectively was — runs the payload and the
/// marker appears; then the same repository is driven through <see cref="AgentGitCommand"/> and the
/// marker must not appear again. A test that only asserted the negative would pass just as happily if
/// git had stopped supporting filters, or if the attribute never matched.</para>
///
/// <para>The payload command is <c>git config</c> rather than a shell utility: git spawns a filter
/// through a shell on every platform, and <c>git</c> is by definition on the PATH of a machine running
/// these tests, so the proof does not depend on <c>touch</c>/<c>sh</c> existing.</para>
/// </summary>
public sealed class AgentGitCommandHardeningTests : IDisposable
{
    private readonly string _root = AgentTestGit.NewVmRoot();

    public void Dispose() => AgentTestGit.DeleteTree(_root);

    [Fact]
    public void HostileFilterDriver_IsNotExecuted_WhenTheDaemonStagesTheWorktree()
    {
        var repo = NewRepo("filter");
        PlantAttributes(repo, "* filter=pwn");
        // A clean filter runs on `git add`/`git status`/`git diff` — i.e. on every leg of the
        // keep-alive cycle the audit measured.
        AgentTestGit.RunChecked(repo, "config", "filter.pwn.clean", MarkerCommand("clean"));
        AgentTestGit.RunChecked(repo, "config", "filter.pwn.smudge", MarkerCommand("smudge"));

        // Non-vacuous: unhardened git executes it.
        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v1\n");
        AgentTestGit.RunChecked(repo, "add", "-A");
        Assert.Equal("yes", Marker(repo, "clean"));

        ClearMarkers(repo);

        // The daemon's own path must not.
        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v2\n");
        AgentGitCommand.Run(repo, "add", "-A");
        Assert.Null(Marker(repo, "clean"));

        // …nor on the status probe the rebaser opens every cycle with, nor on a checkout's smudge leg.
        AgentGitCommand.Run(repo, "status", "--porcelain");
        AgentGitCommand.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "wip: sync");
        AgentGitCommand.Run(repo, "checkout", "--", "payload.txt");
        Assert.Null(Marker(repo, "clean"));
        Assert.Null(Marker(repo, "smudge"));
    }

    [Fact]
    public void HostileTextconvDriver_IsNotExecuted_WhenTheDaemonDiffs()
    {
        var repo = NewRepo("textconv");
        PlantAttributes(repo, "* diff=pwn");
        AgentTestGit.RunChecked(repo, "config", "diff.pwn.textconv", MarkerCommand("textconv"));

        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v1\n");
        AgentTestGit.RunChecked(repo, "add", "-A");
        AgentTestGit.RunChecked(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "seed");
        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v2\n");

        // Non-vacuous: `git diff` applies textconv by default.
        AgentTestGit.RunChecked(repo, "diff");
        Assert.Equal("yes", Marker(repo, "textconv"));

        ClearMarkers(repo);

        // The daemon path neutralizes it. Note the deliberate trade documented on NeutralizingArgs: for
        // the diff.* family git reads an empty value as a command to RUN rather than as "none", so the
        // diff FAILS instead of coming back plain. The agent's command still never runs — which is the
        // property that matters — and only a repository that planted an executable diff driver is
        // affected. `CleanRepository_StillProducesARealDiff_ThroughTheDaemonPath` is the other half.
        AgentGitCommand.TryRun(repo, out _, "diff");
        Assert.Null(Marker(repo, "textconv"));
    }

    /// <summary>
    /// W1-A rework, blocker 1 — the enumeration reads ONE repository's config, and git does not stay in
    /// one repository.
    ///
    /// <para><c>git status --porcelain</c> (the first thing every keep-alive cycle runs, and
    /// <c>WorktreeManager.IsDirty</c>) calls <c>is_submodule_modified()</c> for every POPULATED gitlink —
    /// a path staged as mode 160000 whose <c>&lt;path&gt;/.git</c> exists. <b>No <c>.gitmodules</c> is
    /// needed</b>, which is what makes it reachable from a jail in four ordinary commands. That spawns
    /// <c>git status --porcelain=2</c> inside the nested repository, whose <c>filter.&lt;d&gt;.clean</c>
    /// the daemon's probe never listed and whose <c>GIT_DIR</c> pin <c>prepare_submodule_repo_env()</c>
    /// drops. The nested driver then runs as the daemon user, on the host, outside the jail.</para>
    ///
    /// <para>The fixture is the audit scenario verbatim, and it proves itself non-vacuous first: plain
    /// git executes the nested driver, then the same tree is driven through
    /// <see cref="AgentGitCommand"/> and it must not.</para>
    /// </summary>
    [Fact]
    public void HostileFilterInANestedRepository_IsNotExecuted_WhenTheDaemonProbesTheWorktree()
    {
        var (parent, sub) = NestedHostileRepo("nested");

        // Non-vacuous: unhardened git recurses into the embedded repository and runs its clean filter.
        AgentTestGit.RunChecked(parent, "status", "--porcelain");
        Assert.Equal("yes", Marker(sub, "clean"));

        ClearMarkers(sub);

        // The daemon's own path must not — on every leg of the keep-alive cycle, asserted one at a time so
        // a regression names the subcommand that reopened the hole.
        AgentGitCommand.Run(parent, "status", "--porcelain");
        Assert.Null(Marker(sub, "clean"));
        AgentGitCommand.TryRun(parent, out _, "diff");
        Assert.Null(Marker(sub, "clean"));
        AgentGitCommand.TryRun(parent, out _, "diff", "--cached", "--quiet");
        Assert.Null(Marker(sub, "clean"));
        // …and `add` must still stage ORDINARY work while it skips the gitlink, or the fix would have
        // traded a host RCE for a keep-alive cycle that snapshots nothing.
        File.WriteAllText(Path.Combine(parent, "ordinary.txt"), "real work\n");
        AgentGitCommand.Run(parent, "add", "-A");
        Assert.Null(Marker(sub, "clean"));
        Assert.Contains(
            "ordinary.txt",
            AgentGitCommand.Run(parent, "diff", "--cached", "--name-only"),
            StringComparison.Ordinal);

        AgentGitCommand.Run(parent, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "wip: sync");
        Assert.Null(Marker(sub, "clean"));
    }

    /// <summary>
    /// The cost of blocker 1's fix, stated as a test rather than as a comment, so that a later reader
    /// finds the trade measured instead of rediscovering it as a bug.
    ///
    /// <para>Turning submodule recursion off means daemon-side git no longer SEES a submodule pointer
    /// move: the keep-alive cycle's <c>IsDirty</c> reads clean for one, and
    /// <c>WorktreeManager.CommitAgentWork</c> reports "nothing to commit". The agent's own in-jail git is
    /// not hardened and records such a change normally, so what is given up is a daemon-side wip SNAPSHOT
    /// of a submodule bump. The thing bought with it is that a repository the agent writes cannot make the
    /// daemon execute an arbitrary command on the host.</para>
    /// </summary>
    [Fact]
    public void SubmodulePointerMoves_AreInvisibleToDaemonGit_TheMeasuredCostOfTheSubmodulePins()
    {
        var (parent, sub) = NestedHostileRepo("nested-cost");
        // A NEW path, not an edit: the nested repo's own clean filter writes nothing to stdout, so every
        // blob in it stages empty and editing a tracked file changes no tree. Adding a path does.
        File.WriteAllText(Path.Combine(sub, "g.txt"), "moved\n");
        AgentTestGit.RunChecked(sub, "add", "-A");
        AgentTestGit.RunChecked(sub, "commit", "-q", "-m", "the submodule moved");

        // Plain git sees the gitlink move; the daemon's path deliberately does not.
        Assert.Contains("sub", AgentTestGit.RunChecked(parent, "status", "--porcelain"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, AgentGitCommand.Run(parent, "status", "--porcelain").Trim());
    }

    /// <summary>
    /// Why the always-on <c>-c diff.ignoreSubmodules=all</c> is not on its own enough, and the
    /// command-line <c>--ignore-submodules=all</c> is not redundant with it.
    ///
    /// <para>A config pin is a DEFAULT. <c>set_diffopt_flags_from_submodule_config()</c> looks the path up
    /// in <c>.gitmodules</c> and then applies <c>submodule.&lt;name&gt;.ignore</c> through
    /// <c>handle_ignore_submodules_arg()</c>, which CLEARS <c>ignore_submodules</c> before applying its own
    /// value — so an agent that writes a two-line <c>.gitmodules</c> and one inert config key re-arms the
    /// probe the pin disarmed. <c>submodule.&lt;n&gt;.ignore</c> executes nothing, so the enumeration has
    /// no business neutralizing it. Only the command-line option also sets
    /// <c>override_submodule_config</c>, which makes that function a no-op.</para>
    /// </summary>
    [Fact]
    public void NestedRepositoryFilter_IsStillNotExecuted_WhenTheRepoReEnablesSubmoduleDiffs()
    {
        var (parent, sub) = NestedHostileRepo("nested-override");

        // The re-arm: name the embedded repo as a submodule and declare it un-ignorable.
        File.WriteAllText(
            Path.Combine(parent, ".gitmodules"),
            "[submodule \"sub\"]\n\tpath = sub\n\turl = ./sub\n");
        AgentTestGit.RunChecked(parent, "config", "submodule.sub.ignore", "none");

        AgentTestGit.RunChecked(parent, "status", "--porcelain");
        Assert.Equal("yes", Marker(sub, "clean"));

        ClearMarkers(sub);

        AgentGitCommand.Run(parent, "status", "--porcelain");
        AgentGitCommand.TryRun(parent, out _, "diff");
        AgentGitCommand.Run(parent, "add", "-A");
        AgentGitCommand.TryRun(parent, out _, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "wip: sync");
        Assert.Null(Marker(sub, "clean"));
    }

    /// <summary>
    /// The fetch leg of the same hole, and the one per-submodule key the enumeration cannot reach.
    ///
    /// <para><c>submodule.&lt;name&gt;.fetchRecurseSubmodules</c> is a boolean, not a command, so
    /// <see cref="GitConfigExecutionSurface.IsCommandExecuting"/> classifies it as inert and
    /// <c>NeutralizingArgs</c> — which only neutralizes command-EXECUTING keys — never emits an override
    /// for it. git's own <c>get_fetch_recurse_config()</c> says the key overrules "everything except
    /// commandline". So the question this test answers is whether a fetch in a repository the agent
    /// writes can be talked into spawning a child git inside a NESTED repository, whose config was never
    /// enumerated and therefore never neutralized.</para>
    ///
    /// <para>The payload is <c>remote.origin.uploadpack</c> in the nested repository rather than a
    /// <c>filter.*</c> driver, because a fetch converts no blobs: what a recursive fetch actually runs
    /// from the child's un-enumerated config is the program it spawns — through a shell — to serve a
    /// local-path fetch. Same class of defect, same trust boundary, the shape that really fires.</para>
    ///
    /// <para><b>What this measured.</b> With the daemon's pins in place the payload does not run — and it
    /// does not run with EITHER <c>fetch.recurseSubmodules=false</c> or <c>submodule.recurse=false</c>
    /// alone, because <c>builtin/fetch.c</c> gates the whole of <c>fetch_submodules()</c> on the merged
    /// value and so never reaches the per-submodule lookup. The reported bypass was therefore already
    /// closed. The <c>--recurse-submodules=no</c> flag was added anyway, and the third leg below is what
    /// makes it worth its argument: it asserts the invariant survives BOTH config pins being removed, so
    /// the property no longer depends on a precedence fact about one git version.</para>
    /// </summary>
    [Fact]
    public void NestedRepositoryUploadPack_IsNotExecuted_WhenTheDaemonFetches()
    {
        var (parent, sub, upstream) = NestedFetchHostileRepo("nested-fetch");

        // Non-vacuous: unhardened git honours the per-submodule override and spawns a child fetch inside
        // the nested repository, which runs the command that repository's config names.
        AgentTestGit.Run(parent, "fetch", upstream, "+refs/heads/*:refs/remotes/plain/*");
        Assert.Equal("yes", Marker(sub, "fetch"));

        ClearMarkers(sub);

        // The daemon's own path must not — and must still do the fetch it was asked for.
        Assert.Equal(0, AgentGitCommand.TryRun(parent, out _, "fetch", upstream, "+refs/heads/*:refs/remotes/daemon/*"));
        Assert.Null(Marker(sub, "fetch"));
        Assert.Contains(
            "refs/remotes/daemon/",
            AgentTestGit.RunChecked(parent, "for-each-ref", "--format=%(refname)"),
            StringComparison.Ordinal);

        // Why the flag earns its argument, stated as a measurement rather than as a claim. The first leg
        // above is this one's negative control: the SAME unhardened git, the same repository, differing
        // only by `--recurse-submodules=no` — and that one alone, with neither config pin in play, is
        // enough. So the invariant no longer rests on git's config precedence order.
        AgentTestGit.Run(
            parent, "fetch", "--recurse-submodules=no", upstream, "+refs/heads/*:refs/remotes/flagonly/*");
        Assert.Null(Marker(sub, "fetch"));
    }

    /// <summary>The submodule pins must not have broken ordinary daemon git: a worktree with a REAL
    /// change still reads dirty through the same probe the rebaser gates its wip commit on.</summary>
    [Fact]
    public void OrdinaryDirtyWorktree_IsStillReportedDirty_WithTheSubmodulePinsOn()
    {
        var (parent, _) = NestedHostileRepo("nested-still-dirty");
        File.WriteAllText(Path.Combine(parent, "ordinary.txt"), "a change the daemon must still see\n");

        Assert.Contains("ordinary.txt", AgentGitCommand.Run(parent, "status", "--porcelain"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The families that cannot be reached from a two-line fixture (<c>merge.&lt;d&gt;.driver</c> needs
    /// a real conflicting rebase, <c>uploadpack.packObjectsHook</c> needs a serving process) are pinned
    /// on the pure classifier instead, so the list is a tested artifact rather than a comment.
    /// </summary>
    [Theory]
    [InlineData("filter.lfs.clean")]
    [InlineData("filter.lfs.smudge")]
    [InlineData("filter.a.process")]
    [InlineData("merge.ours.driver")]
    [InlineData("diff.external")]
    [InlineData("diff.jpg.command")]
    [InlineData("diff.jpg.textconv")]
    [InlineData("difftool.x.cmd")]
    [InlineData("mergetool.x.cmd")]
    [InlineData("core.hooksPath")]
    [InlineData("core.fsmonitor")]
    [InlineData("core.sshCommand")]
    [InlineData("core.pager")]
    [InlineData("core.editor")]
    [InlineData("core.gitProxy")]
    [InlineData("core.askPass")]
    [InlineData("core.alternateRefsCommand")]
    [InlineData("sequence.editor")]
    [InlineData("credential.helper")]
    [InlineData("credential.https://evil.example.com.helper")]
    [InlineData("remote.origin.uploadpack")]
    [InlineData("remote.origin.receivepack")]
    [InlineData("uploadpack.packObjectsHook")]
    [InlineData("init.templateDir")]
    [InlineData("gpg.program")]
    [InlineData("gpg.ssh.program")]
    // W1-A rework, blocker 3. `commit.gpgsign=true` + `gpg.format=ssh` + no `user.signingkey` makes git
    // run this THROUGH A SHELL to discover a key — on every commit the daemon makes in the agent's
    // repository, and on every commit a rebase replays. Repo-local, so the agent writes all four.
    [InlineData("gpg.ssh.defaultKeyCommand")]
    [InlineData("gpg.x509.program")]
    [InlineData("trailer.sign.command")]
    [InlineData("submodule.sub.update")]
    [InlineData("pager.log")]
    public void CommandExecutingKeys_AreClassifiedAsSuch(string key)
        => Assert.True(GitConfigExecutionSurface.IsCommandExecuting(key), key);

    /// <summary>
    /// The other half of the contract: the keys the product itself writes must NOT be neutralized, or
    /// the belt would quietly break provisioning. These are exactly the repo-local keys
    /// <c>RepoProvisioner</c>/<c>AgentRepo</c>/<c>MirrorMaintenance</c> set.
    /// </summary>
    [Theory]
    [InlineData("core.sharedRepository")]
    [InlineData("core.untrackedCache")]
    [InlineData("core.bare")]
    [InlineData("core.repositoryformatversion")]
    [InlineData("gc.auto")]
    [InlineData("maintenance.auto")]
    [InlineData("receive.denyNonFastForwards")]
    [InlineData("receive.denyDeletes")]
    [InlineData("remote.origin.url")]
    [InlineData("remote.origin.fetch")]
    [InlineData("user.name")]
    [InlineData("user.email")]
    [InlineData("filter.lfs.required")]
    [InlineData("alias.st")]
    [InlineData("extensions.worktreeConfig")]
    public void ProductAndInertKeys_AreLeftAlone(string key)
        => Assert.False(GitConfigExecutionSurface.IsCommandExecuting(key), key);

    /// <summary>
    /// A subsection name may legally contain <c>=</c>, and <c>git -c</c> splits at the first one — so
    /// the override would silently land on a different key while the real driver stayed live. That is
    /// the one outcome worse than no override, so it is a typed refusal and git never runs.
    /// </summary>
    [Fact]
    public void UnexpressibleKeyName_IsRefused_RatherThanSilentlyMisapplied()
    {
        Assert.False(GitConfigExecutionSurface.IsExpressibleOnCommandLine("filter.a=b.clean"));
        Assert.False(GitConfigExecutionSurface.IsExpressibleOnCommandLine("filter.a b.clean"));
        Assert.True(GitConfigExecutionSurface.IsExpressibleOnCommandLine("filter.ab.clean"));

        var repo = NewRepo("unexpressible");
        // Written straight into the config file: `git config` would reject the name too.
        File.AppendAllText(
            Path.Combine(repo, ".git", "config"),
            "\n[filter \"a=b\"]\n\tclean = " + MarkerCommand("clean") + "\n");

        var ex = Assert.Throws<RepoProvisioningException>(() => AgentGitCommand.Run(repo, "status", "--porcelain"));
        Assert.Contains("cannot be overridden", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The belt must not have made ordinary daemon git any less usable: a clean repository
    /// still stages, commits and reports through the same path.</summary>
    [Fact]
    public void OrdinaryDaemonGit_StillWorks()
    {
        var repo = NewRepo("ordinary");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "hello\n");
        AgentGitCommand.Run(repo, "add", "-A");
        AgentGitCommand.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "seed");
        Assert.Equal(string.Empty, AgentGitCommand.Run(repo, "status", "--porcelain").Trim());
        Assert.Equal(0, AgentGitCommand.TryRun(repo, out var sha, "rev-parse", "HEAD"));
        Assert.Equal(40, sha.Trim().Length);
    }

    /// <summary>
    /// The regression this suite's first draft shipped and <c>MergeQueueProvisionerTests</c> caught.
    ///
    /// <para>The belt originally pinned the fixed-name command knobs empty unconditionally, on the
    /// assumption that empty means "disabled" — which holds for <c>filter.*</c> and does NOT hold for
    /// <c>diff.external</c>: git reads an empty value there as a command to run, so every daemon-side
    /// <c>git diff</c> on a perfectly ordinary repository died with <c>error: cannot run :</c>. The
    /// merge queue's flagged-change gate consumes that diff, so its failure surfaced as
    /// <c>CanMerge</c> quietly going false. An always-on pin must be correct for a repository that
    /// declares nothing; this test is what says so.</para>
    /// </summary>
    [Fact]
    public void CleanRepository_StillProducesARealDiff_ThroughTheDaemonPath()
    {
        var repo = NewRepo("diffable");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n");
        AgentGitCommand.Run(repo, "add", "-A");
        AgentGitCommand.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "seed");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

        var patch = AgentGitCommand.Run(repo, "diff");

        Assert.Contains("diff --git a/a.txt b/a.txt", patch, StringComparison.Ordinal);
        Assert.Contains("+two", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot run", patch, StringComparison.Ordinal);
    }

    // ---- fixture -------------------------------------------------------------

    private string NewRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        AgentTestGit.RunChecked(path, "init", "-q");
        AgentTestGit.SetIdentity(path);
        return path;
    }

    private static void PlantAttributes(string repo, string line)
        => File.WriteAllText(Path.Combine(repo, ".gitattributes"), line + "\n");

    /// <summary>
    /// The audit's nested-repository payload, built with the five commands a jailed agent can run in its
    /// own worktree — <c>git init sub</c>, one config key, one <c>.gitattributes</c> line, a commit in the
    /// sub, and <c>git add sub</c> in the parent. Returns (parent worktree, nested repository).
    ///
    /// <para>The nested file is left MODIFIED on purpose: git compares a tracked file by stat first and
    /// only converts its content (i.e. runs the clean filter) when the stat differs. A pristine nested
    /// repo would make the test vacuous in the one direction that matters.</para>
    /// </summary>
    private (string Parent, string Sub) NestedHostileRepo(string name)
    {
        var parent = NewRepo(name);
        File.WriteAllText(Path.Combine(parent, "a.txt"), "parent\n");
        AgentTestGit.RunChecked(parent, "add", "-A");
        AgentTestGit.RunChecked(parent, "commit", "-q", "-m", "seed");

        var sub = Path.Combine(parent, "sub");
        Directory.CreateDirectory(sub);
        AgentTestGit.RunChecked(sub, "init", "-q");
        AgentTestGit.SetIdentity(sub);
        AgentTestGit.RunChecked(sub, "config", "filter.pwn.clean", MarkerCommand("clean"));
        AgentTestGit.RunChecked(sub, "config", "filter.pwn.smudge", MarkerCommand("smudge"));
        PlantAttributes(sub, "* filter=pwn");
        File.WriteAllText(Path.Combine(sub, "f.txt"), "v1\n");
        AgentTestGit.RunChecked(sub, "add", "-A");
        AgentTestGit.RunChecked(sub, "commit", "-q", "-m", "nested seed");
        ClearMarkers(sub);

        // The gitlink. `add` warns about an embedded repository and stages it anyway, which is the point.
        AgentTestGit.RunChecked(parent, "add", "sub");
        AgentTestGit.RunChecked(parent, "commit", "-q", "-m", "embed");

        // Dirty the nested worktree so the child status has content to convert.
        File.WriteAllText(Path.Combine(sub, "f.txt"), "v2\n");
        ClearMarkers(sub);
        return (parent, sub);
    }

    /// <summary>
    /// The fetch-path variant of <see cref="NestedHostileRepo"/>: a populated gitlink that is a REAL
    /// submodule (named in <c>.gitmodules</c>, with a remote of its own), the per-submodule recursion
    /// override in the superproject config, and the payload in the nested repository's
    /// <c>remote.origin.uploadpack</c> — the command git spawns, through a shell, to serve a fetch from a
    /// local path. Returns (parent worktree, nested repository, the upstream to fetch from).
    ///
    /// <para>The submodule is <c>clone</c>d rather than <c>submodule add</c>ed: modern git refuses a
    /// <c>file://</c> submodule URL without <c>protocol.file.allow</c>, and the product never sets that
    /// (it is a rejection trigger). The shape on disk is identical, which is all recursion looks at.</para>
    ///
    /// <para>The upstream is an exact copy of the parent — no new commits. With
    /// <c>fetchRecurseSubmodules=true</c> the recursion is unconditional (<c>get_next_submodule()</c>
    /// takes the <c>RECURSE_SUBMODULES_ON</c> arm without consulting <c>changed_submodule_names</c>), so a
    /// no-op fetch still spawns the child. Measured, not assumed.</para>
    /// </summary>
    private (string Parent, string Sub, string Upstream) NestedFetchHostileRepo(string name)
    {
        var baseDir = Path.Combine(_root, name);
        Directory.CreateDirectory(baseDir);

        // The submodule's own upstream, so the child fetch has a remote to dial.
        var nestedSource = Path.Combine(baseDir, "nested-source");
        Directory.CreateDirectory(nestedSource);
        AgentTestGit.RunChecked(nestedSource, "init", "-q");
        AgentTestGit.SetIdentity(nestedSource);
        File.WriteAllText(Path.Combine(nestedSource, "f.txt"), "v1\n");
        AgentTestGit.RunChecked(nestedSource, "add", "-A");
        AgentTestGit.RunChecked(nestedSource, "commit", "-q", "-m", "nested seed");
        var nestedUpstream = Path.Combine(baseDir, "nested-upstream.git");
        AgentTestGit.RunChecked(baseDir, "clone", "--bare", "-q", nestedSource, nestedUpstream);

        var parent = NewRepo(Path.Combine(name, "parent"));
        File.WriteAllText(Path.Combine(parent, "a.txt"), "parent\n");
        AgentTestGit.RunChecked(parent, "add", "-A");
        AgentTestGit.RunChecked(parent, "commit", "-q", "-m", "seed");

        var sub = Path.Combine(parent, "sub");
        AgentTestGit.RunChecked(parent, "clone", "-q", nestedUpstream, sub);
        AgentTestGit.SetIdentity(sub);
        AgentTestGit.RunChecked(sub, "config", "remote.origin.uploadpack", MarkerCommand("fetch"));

        File.WriteAllText(
            Path.Combine(parent, ".gitmodules"),
            "[submodule \"sub\"]\n\tpath = sub\n\turl = " + nestedUpstream.Replace('\\', '/') + "\n");
        AgentTestGit.RunChecked(parent, "add", "-A");
        AgentTestGit.RunChecked(parent, "commit", "-q", "-m", "embed");

        // The key this test exists for. It is inert (a boolean), so the W1-A enumeration leaves it alone.
        AgentTestGit.RunChecked(parent, "config", "submodule.sub.fetchRecurseSubmodules", "true");

        var upstream = Path.Combine(baseDir, "upstream.git");
        AgentTestGit.RunChecked(baseDir, "clone", "--bare", "-q", parent, upstream);

        ClearMarkers(sub);
        return (parent, sub, upstream);
    }

    /// <summary>The payload: a command git will spawn through a shell, whose only effect is a
    /// config marker this test can read back. Never a shell utility — <c>git</c> is the one binary
    /// guaranteed present.</summary>
    private static string MarkerCommand(string marker) => $"git config --local pwned.{marker} yes";

    private static string? Marker(string repo, string marker)
    {
        var (code, output, _) = AgentTestGit.Run(repo, "config", "--local", "--get", "pwned." + marker);
        return code == 0 ? output.Trim() : null;
    }

    private static void ClearMarkers(string repo)
        => AgentTestGit.Run(repo, "config", "--local", "--remove-section", "pwned");
}
