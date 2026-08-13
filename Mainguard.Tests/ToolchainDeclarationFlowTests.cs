using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.Tests.TestTools;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Tests;

/// <summary>
/// Settings → Toolchains → "Declare a toolchain in this repository": the four-step, one-button-per-step
/// flow. Every test drives the REAL <see cref="ToolchainDeclarationViewModel"/> against a REAL temporary
/// git repository through the REAL <see cref="GitService"/> — the shipped staging/commit path executes
/// unchanged, so what is asserted is what the repository ends up containing, not what the view model
/// believes it did.
///
/// <para>The bar these hold is the one the owner set: the app performs each step only when its own
/// button is pressed, it never performs another step as a side effect (write does not stage, commit does
/// not push), it never rewrites the user's working tree or HEAD to make itself possible (no stash, no
/// checkout), it resolves the default branch instead of assuming one (this repository's is
/// <c>master</c>), and no button can ever be disabled without a sentence saying why — the exact defect
/// #302 shipped.</para>
/// </summary>
public class ToolchainDeclarationFlowTests
{
    private const string DeclPath = ToolchainDeclarationViewModel.DeclarationPath;

    // ---- default branch resolution ---------------------------------------------------------------

    [Fact]
    public async Task DefaultBranch_ShouldBeResolvedDynamically_SoAMasterRepoIsOnItsDefault()
    {
        // The owner's exact case. A hardcoded "main" makes this repository permanently un-declarable,
        // with the commit step refusing for a reason that is simply false.
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();

        Assert.Equal("master", vm.CurrentBranch);
        Assert.Equal("master", vm.DefaultBranch);
        Assert.True(vm.IsOnDefaultBranch,
            $"a repository on 'master' was not treated as being on its default branch — the flow resolved "
            + $"'{vm.DefaultBranch}' instead.");
        Assert.DoesNotContain("main", vm.BranchSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultBranch_ShouldComeFromTheRemoteHead_WhenTheCloneRecordsOne()
    {
        // A clone's refs/remotes/<remote>/HEAD is the only local authority that can disagree with the
        // branch you are standing on, so it is what makes "you are not on the default branch" knowable.
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");
        fx.AddBareRemote();
        SetRemoteHead(fx.RepoPath, "origin", "master");

        fx.CreateBranch("feature/x");
        fx.Checkout("feature/x");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();

        Assert.Equal("feature/x", vm.CurrentBranch);
        Assert.Equal("master", vm.DefaultBranch);
        Assert.False(vm.IsOnDefaultBranch);
    }

    // ---- refusals that never act on the user's behalf ---------------------------------------------

    [Fact]
    public async Task DirtyWorkingTree_ShouldDisableCommit_WithAStatedReason_AndNeverStash()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");
        fx.WriteFile("someone-elses-work.txt", "do not touch\n");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();
        await vm.WriteFileCommand.ExecuteAsync(null); // step 1 is legal even with a dirty tree

        Assert.False(vm.CommitCommand.CanExecute(null));
        Assert.NotEqual(string.Empty, vm.CommitDisabledReason);
        Assert.Contains("someone-elses-work.txt", vm.CommitDisabledReason, StringComparison.Ordinal);
        Assert.Contains("stash", vm.CommitDisabledReason, StringComparison.OrdinalIgnoreCase);

        // Nothing of the user's was moved out of the way to make the flow possible.
        Assert.Equal("do not touch\n", File.ReadAllText(Path.Combine(fx.RepoPath, "someone-elses-work.txt")));
        using var repo = new Repository(fx.RepoPath);
        Assert.Empty(repo.Stashes);
    }

    [Fact]
    public async Task NonDefaultBranch_ShouldDisableCommit_NamingBothBranches_AndNeverCheckOut()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");
        fx.AddBareRemote();
        SetRemoteHead(fx.RepoPath, "origin", "master");
        fx.CreateBranch("feature/x");
        fx.Checkout("feature/x");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();
        await vm.WriteFileCommand.ExecuteAsync(null);

        Assert.False(vm.CommitCommand.CanExecute(null));
        Assert.Contains("feature/x", vm.CommitDisabledReason, StringComparison.Ordinal);
        Assert.Contains("master", vm.CommitDisabledReason, StringComparison.Ordinal);

        // HEAD is exactly where the user left it — the flow refused, it did not "help".
        using var repo = new Repository(fx.RepoPath);
        Assert.Equal("feature/x", repo.Head.FriendlyName);
    }

    // ---- step 1: the working tree, and only the working tree --------------------------------------

    [Fact]
    public async Task WriteFile_ShouldWriteTheWorkingTreeOnly_AndStageNothing()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();
        var commitsBefore = CommitCount(fx.RepoPath);

        await vm.WriteFileCommand.ExecuteAsync(null);

        Assert.Equal("python-3\n", File.ReadAllText(Path.Combine(fx.RepoPath, ".mainguard", "toolchain")));
        Assert.False(vm.DeclarationIsStaged,
            "Write file staged the declaration — step 1 must touch the working tree and nothing else.");
        Assert.Equal(commitsBefore, CommitCount(fx.RepoPath));

        // Measured against the repository, not the view model: the index must be untouched.
        using var repo = new Repository(fx.RepoPath);
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true });
        var entry = status.Single(e => e.FilePath == DeclPath);
        Assert.Equal(FileStatus.NewInWorkdir, entry.State);
        Assert.Null(repo.Index[DeclPath]);
    }

    // ---- step 2: exactly one commit, exactly one path, and no push --------------------------------

    [Fact]
    public async Task Commit_ShouldMakeExactlyOneCommitOfExactlyTheDeclaration_AndNotPush()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");
        var barePath = fx.AddBareRemote();
        fx.SetUpstream("origin");
        var remoteTipBefore = RemoteTip(barePath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();
        await vm.WriteFileCommand.ExecuteAsync(null);

        var commitsBefore = CommitCount(fx.RepoPath);
        Assert.True(vm.CommitCommand.CanExecute(null), vm.CommitDisabledReason);
        // The message and the branch are on screen BEFORE the commit runs — nothing about it is a surprise.
        var announced = vm.CommitMessage;
        Assert.Contains("python-3", announced, StringComparison.Ordinal);
        Assert.Equal("master", vm.CurrentBranch);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(commitsBefore + 1, CommitCount(fx.RepoPath));
        using (var repo = new Repository(fx.RepoPath))
        {
            var tip = repo.Head.Tip;
            Assert.Equal(announced, tip.Message.Trim());
            var changes = repo.Diff.Compare<TreeChanges>(tip.Parents.Single().Tree, tip.Tree);
            Assert.Equal(new[] { DeclPath }, changes.Select(c => c.Path).ToArray());
        }

        // The remote never moved: committing is not pushing, and the flow has no path that makes it so.
        Assert.Equal(remoteTipBefore, RemoteTip(barePath, "master"));
        Assert.False(vm.DeclarationHasUncommittedChange);
        Assert.NotEqual(string.Empty, vm.CommitDisabledReason); // nothing left to commit — and it says so
    }

    [Fact]
    public async Task NothingToCommit_ShouldSayExactlyThat()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();

        Assert.False(vm.CommitCommand.CanExecute(null));
        Assert.Contains("nothing to commit", vm.CommitDisabledReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DeclPath, vm.CommitDisabledReason, StringComparison.Ordinal);
    }

    // ---- step 3: push is its own decision ---------------------------------------------------------

    [Fact]
    public async Task Push_ShouldRefuseWithAReason_WhenThereIsNoRemote()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();

        Assert.False(vm.PushCommand.CanExecute(null));
        Assert.Contains("no remote", vm.PushDisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Push_ShouldBecomeAvailableOnlyAfterTheCommitStepRan()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");
        fx.AddBareRemote();
        fx.SetUpstream("origin");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();

        // Up to date with the remote: there is genuinely nothing to push, and that is what it says.
        Assert.False(vm.PushCommand.CanExecute(null));
        Assert.Contains("Nothing to push", vm.PushDisabledReason, StringComparison.OrdinalIgnoreCase);

        await vm.WriteFileCommand.ExecuteAsync(null);
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.PushCommand.CanExecute(null), vm.PushDisabledReason);
        Assert.Equal(string.Empty, vm.PushDisabledReason);
    }

    // ---- step 4: install the DECLARED toolchain ---------------------------------------------------

    [Fact]
    public async Task Install_ShouldBeRefusedUntilSomethingIsDeclared_ThenInstallTheDeclaredId()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out var host);
        await vm.RefreshAsync();

        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Contains(DeclPath, vm.InstallDisabledReason, StringComparison.Ordinal);
        Assert.Contains("does not declare", vm.InstallDisabledReason, StringComparison.OrdinalIgnoreCase);

        await vm.WriteFileCommand.ExecuteAsync(null);

        Assert.Equal("python-3", vm.DeclaredToolchainId);
        Assert.True(vm.InstallCommand.CanExecute(null), vm.InstallDisabledReason);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "python-3" }, host.InstalledIds);
        Assert.True(vm.DeclaredToolchainInstalled);
        // Already there → refused, and the refusal is the reason.
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Contains("already installed", vm.InstallDisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the #302 invariant -----------------------------------------------------------------------

    [Fact]
    public async Task NoStepCanEverBeDisabledWithoutAReason()
    {
        // Swept across the states this flow actually passes through, including the ones where several
        // steps are refused at once. A disabled control with no explanation is a defect in this repo.
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");
        fx.AddBareRemote();
        SetRemoteHead(fx.RepoPath, "origin", "master");
        fx.SetUpstream("origin");

        var vm = NewViewModel(fx.RepoPath, out _);

        await vm.RefreshAsync();
        AssertEveryDisabledStepExplainsItself(vm, "freshly opened");

        await vm.WriteFileCommand.ExecuteAsync(null);
        AssertEveryDisabledStepExplainsItself(vm, "after writing the file");

        await vm.CommitCommand.ExecuteAsync(null);
        AssertEveryDisabledStepExplainsItself(vm, "after committing");

        await vm.InstallCommand.ExecuteAsync(null);
        AssertEveryDisabledStepExplainsItself(vm, "after installing");

        fx.CreateBranch("feature/y");
        fx.Checkout("feature/y");
        fx.WriteFile("dirty.txt", "x\n");
        await vm.RefreshAsync();
        AssertEveryDisabledStepExplainsItself(vm, "on a non-default branch with a dirty tree");

        // And with no repository at all, which is the state a Settings page opens in most often.
        var orphan = new ToolchainDeclarationViewModel(null, new GitService(), NewChannel(new FakeHost()));
        await orphan.RefreshAsync();
        AssertEveryDisabledStepExplainsItself(orphan, "with no repository open");
    }

    [Fact]
    public async Task EveryStepCommand_ShouldRepublishCanExecute_WhenItsReasonChanges()
    {
        // The bug class this repo shipped: a predicate whose inputs change without CanExecuteChanged
        // leaves a Button rendered visible and permanently dead.
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();

        Assert.False(vm.CommitCommand.CanExecute(null));
        var raised = 0;
        vm.CommitCommand.CanExecuteChanged += (_, _) => raised++;

        await vm.WriteFileCommand.ExecuteAsync(null);

        Assert.True(vm.CommitCommand.CanExecute(null), vm.CommitDisabledReason);
        Assert.True(raised > 0,
            "CommitCommand never raised CanExecuteChanged when writing the file made it possible — "
            + "the button shows but stays disabled.");
    }

    [Fact]
    public async Task Refresh_ShouldReMeasureTheRepository_NotRememberIt()
    {
        // Preconditions are re-evaluated after every action; a change made outside the app must be
        // visible on the next measurement, or the flow can show stale state.
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "seed\n", "seed");
        RenameCurrentBranch(fx.RepoPath, "master");

        var vm = NewViewModel(fx.RepoPath, out _);
        await vm.RefreshAsync();
        await vm.WriteFileCommand.ExecuteAsync(null);
        Assert.True(vm.CommitCommand.CanExecute(null), vm.CommitDisabledReason);

        // Someone deletes the file behind the app's back.
        File.Delete(Path.Combine(fx.RepoPath, ".mainguard", "toolchain"));
        await vm.RefreshAsync();

        Assert.Null(vm.WorkingTreeDeclaration);
        Assert.False(vm.CommitCommand.CanExecute(null));
        Assert.NotEqual(string.Empty, vm.CommitDisabledReason);
        Assert.True(vm.WriteFileCommand.CanExecute(null), vm.WriteFileDisabledReason);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static void AssertEveryDisabledStepExplainsItself(ToolchainDeclarationViewModel vm, string state)
    {
        var steps = new (string Name, bool CanExecute, string Reason)[]
        {
            ("Write file", vm.WriteFileCommand.CanExecute(null), vm.WriteFileDisabledReason),
            ("Stage & commit", vm.CommitCommand.CanExecute(null), vm.CommitDisabledReason),
            ("Push", vm.PushCommand.CanExecute(null), vm.PushDisabledReason),
            ("Install toolchain", vm.InstallCommand.CanExecute(null), vm.InstallDisabledReason),
        };

        foreach (var (name, canExecute, reason) in steps)
        {
            if (!canExecute)
            {
                Assert.False(string.IsNullOrWhiteSpace(reason),
                    $"'{name}' is disabled {state} with no reason next to it — that is exactly the #302 defect.");
            }
            else
            {
                Assert.True(string.IsNullOrEmpty(reason),
                    $"'{name}' is enabled {state} while still showing a refusal ('{reason}') — the button and "
                    + "the sentence next to it would contradict each other.");
            }
        }
    }

    private static ToolchainDeclarationViewModel NewViewModel(string repoPath, out FakeHost host)
    {
        host = new FakeHost();
        return new ToolchainDeclarationViewModel(repoPath, new GitService(), NewChannel(host));
    }

    private static ToolchainChannel NewChannel(FakeHost host)
    {
        var manifest = ToolchainManifest.Parse(ManifestJson);
        host.Entries = manifest.Entries;
        // The payload source is what the channel fetches from — never `curl` in the VM, which has none.
        return new ToolchainChannel(host, manifest, payloads: new FakeToolchainPayloadSource());
    }

    private static int CommitCount(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Head.Tip is null ? 0 : repo.Commits.Count();
    }

    private static string RemoteTip(string barePath, string branch)
    {
        using var repo = new Repository(barePath);
        return repo.Branches[branch]?.Tip?.Sha ?? "";
    }

    /// <summary>Renames whatever branch HEAD is on, so the fixture's branch name is a test input rather
    /// than whatever <c>init.defaultBranch</c> happens to be on the machine running this.</summary>
    private static void RenameCurrentBranch(string repoPath, string name)
    {
        using var repo = new Repository(repoPath);
        if (string.Equals(repo.Head.FriendlyName, name, StringComparison.Ordinal))
            return;
        repo.Branches.Rename(repo.Head.FriendlyName, name);
    }

    /// <summary>Writes <c>refs/remotes/&lt;remote&gt;/HEAD</c> the way a real clone has it.</summary>
    private static void SetRemoteHead(string repoPath, string remote, string branch)
    {
        using var repo = new Repository(repoPath);
        var target = $"refs/remotes/{remote}/{branch}";
        if (repo.Refs[target] is null)
            repo.Refs.Add(target, repo.Head.Tip.Id);
        repo.Refs.Add($"refs/remotes/{remote}/HEAD", target, allowOverwrite: true);
    }

    private const string PayloadUrl = "https://example.invalid/cpython-3.12.13.tar.gz";

    /// <summary>The REAL hash of the bytes the fake source serves. The channel hashes the payload it
    /// holds, on the host, before the VM sees anything, so a fixture can no longer declare an arbitrary
    /// hex and have a fake VM agree with it.</summary>
    private static readonly string PayloadSha = FakeToolchainPayloadSource.Sha256For(PayloadUrl);

    // A one-entry manifest whose id is also in the sandbox catalog, so a declaration naming it parses
    // (the catalog is what ToolchainDeclarationResolver validates against).
    private static readonly string ManifestJson = $$"""
    {
      "toolchains": [
        {
          "id": "python-3",
          "displayName": "Python 3",
          "summary": "CPython 3.12 with pip — runs a repository's Python test suite.",
          "version": "3.12.13",
          "payloadUrl": "{{PayloadUrl}}",
          "sha256": "{{PayloadSha}}",
          "stripComponents": 1,
          "pathEntries": ["{toolchain}/bin"],
          "probe": {
            "command": ["{toolchain}/bin/python3", "-c", "import pip, sys; print(sys.version)"],
            "expectedVersionSubstring": "3.12.13"
          }
        }
      ]
    }
    """;

    /// <summary>Fake VM for the toolchain channel — the same scripted-by-argv shape
    /// <c>ToolchainSettingsUiTests</c> uses, so the real fetch → verify → unpack → run-it policy executes
    /// here too, minus the VM.</summary>
    private sealed class FakeHost : IAdapterInstallHost
    {
        public IReadOnlyList<ToolchainEntry> Entries = Array.Empty<ToolchainEntry>();

        private readonly Dictionary<string, string> _present = new(StringComparer.Ordinal);

        public IReadOnlyList<string> InstalledIds => _present.Keys.ToList();

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Run(command));
        }

        private AdapterCommandResult Run(IReadOnlyList<string> command)
        {
            var argv0 = command[0];

            if (argv0.StartsWith(ToolchainPaths.VmRoot + "/", StringComparison.Ordinal))
            {
                var probed = IdIn(argv0);
                return _present.TryGetValue(probed, out var reported)
                    ? new AdapterCommandResult(0, reported, "")
                    : new AdapterCommandResult(127, "", $"{argv0}: no such file or directory");
            }

            // A real MainguardEnv has neither curl nor wget; a fake that answers them is how the
            // `curl`-based install path passed CI while failing every user. See MainguardEnvFacts.
            if (MainguardEnvFacts.RefuseIfAbsent(argv0) is { } absent)
                return absent;

            var id = IdIn(string.Join(' ', command));
            switch (argv0)
            {
                case "rm":
                    if (command.Contains(ToolchainPaths.VmInstallDir(id)))
                        _present.Remove(id);
                    return new AdapterCommandResult(0, "", "");

                case "mv":
                    _present[id] = Entries.Single(e => e.Id == id).Version;
                    return new AdapterCommandResult(0, "", "");

                default:
                    return new AdapterCommandResult(0, "", "");
            }
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct) =>
            Task.FromResult($"{ToolchainPaths.VmStageDir}/{fileName}");

        private string IdIn(string text) =>
            Entries.FirstOrDefault(e => text.Contains(e.Id, StringComparison.Ordinal))?.Id ?? "";
    }
}
