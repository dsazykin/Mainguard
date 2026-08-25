using System;
using System.IO;
using System.Linq;
using Mainguard.App.Shell.Services;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Pins <see cref="AutoDetectScan"/> — the directory walk behind the sidebar's "auto-detect
/// repositories" folder browse (<c>MainWindowViewModel.ScanAutoDetectFolderAsync</c>).
/// <para>
/// Regression: walkthrough bug W3. Pointing auto-detect at a folder that IS ITSELF a repository
/// used to walk that repository's own children, and a raw <c>.git</c> directory satisfies libgit2's
/// repository signature, so the sidebar gained an entry labelled ".git" instead of the repo's own
/// folder name. Both guards are pinned here: the root-is-a-repository shortcut, and
/// <see cref="GitService.IsGitRepository"/> refusing a <c>.git</c> directory.
/// </para>
/// The common case — a parent folder of repositories, optionally one grouping level deep — is
/// pinned alongside it so the fix cannot regress it. Real git repos in a temp tree, like
/// <see cref="RepoDiscoveryServiceTests"/>.
/// </summary>
public sealed class AutoDetectScanTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git = new();

    public AutoDetectScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mainguard-autodetect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            // .git objects are read-only on Windows; strip the attribute before deleting.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    // ---- W3: the chosen root is itself a repository ----

    [Fact]
    public void RootThatIsItselfARepo_IsAddedUnderItsOwnName_NeverDotGit()
    {
        // The live repro: the picked folder is a repository with the usual non-repo siblings
        // inside it (`.git`, `.mainguard`, `src`).
        var repo = InitRepo("e2e-fixture");
        PlainDir("e2e-fixture", ".mainguard");
        PlainDir("e2e-fixture", "src");

        var found = AutoDetectScan.Scan(repo, _git.IsGitRepository);

        var single = Assert.Single(found);
        Assert.Equal(repo, single.Path);
        Assert.Equal("e2e-fixture", single.DisplayName); // NOT ".git"
        Assert.Null(single.CategoryName);                // the default category, not a group
        Assert.DoesNotContain(found, r => r.DisplayName == ".git" || r.Path.EndsWith(".git", StringComparison.Ordinal));
    }

    [Fact]
    public void RootThatIsItselfARepo_TrailingSeparatorFromThePicker_StillNamedByItsFolder()
    {
        var repo = InitRepo("with-slash");

        var found = AutoDetectScan.Scan(repo + Path.DirectorySeparatorChar, _git.IsGitRepository);

        var single = Assert.Single(found);
        Assert.Equal(repo, single.Path);
        Assert.Equal("with-slash", single.DisplayName);
    }

    [Fact]
    public void ADotGitDirectory_IsNeverItselfARepository()
    {
        var repo = InitRepo("guarded");
        var gitDir = Path.Combine(repo, ".git");

        // libgit2 answers "valid" for the raw git directory — that is the exact confusion the
        // guard exists for, so pin both halves.
        Assert.True(LibGit2Sharp.Repository.IsValid(gitDir));
        Assert.False(_git.IsGitRepository(gitDir));
    }

    // ---- the common case must keep working exactly as before ----

    [Fact]
    public void ParentFolderOfRepos_ScansTopLevelAndOneGroupingLevel()
    {
        var alpha = InitRepo("alpha");
        var beta = InitRepo("client-work", "beta");
        PlainDir("not-a-repo");
        PlainDir("client-work", "notes"); // non-repo grandchild — skipped

        var found = AutoDetectScan.Scan(_root, _git.IsGitRepository);

        Assert.Equal(2, found.Count);
        var top = Assert.Single(found, r => r.Path == alpha);
        Assert.Equal("alpha", top.DisplayName);
        Assert.Null(top.CategoryName); // top-level repo → the default category

        var nested = Assert.Single(found, r => r.Path == beta);
        Assert.Equal("beta", nested.DisplayName);
        Assert.Equal("client-work", nested.CategoryName); // grouping folder → its own category
    }

    [Fact]
    public void ParentFolderOfRepos_NeverDescendsIntoARepositoryItFound()
    {
        // A repo found at the top level must not contribute its own `.git`/subfolders as extra
        // entries — the same blind spot as W3, one level down.
        InitRepo("alpha");
        PlainDir("alpha", "src");

        var found = AutoDetectScan.Scan(_root, _git.IsGitRepository);

        Assert.Single(found);
        Assert.All(found, r => Assert.NotEqual(".git", r.DisplayName));
    }

    [Fact]
    public void ThirdLevelRepos_AreOutOfScope()
    {
        InitRepo("group", "subgroup", "too-deep");

        Assert.Empty(AutoDetectScan.Scan(_root, _git.IsGitRepository));
    }

    [Fact]
    public void MissingOrBlankRoot_YieldsEmpty_NeverThrows()
    {
        Assert.Empty(AutoDetectScan.Scan(Path.Combine(_root, "does-not-exist"), _git.IsGitRepository));
        Assert.Empty(AutoDetectScan.Scan(string.Empty, _git.IsGitRepository));
        Assert.Empty(AutoDetectScan.Scan("   ", _git.IsGitRepository));
    }

    private string InitRepo(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        LibGit2Sharp.Repository.Init(path);
        return path;
    }

    private string PlainDir(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }
}
