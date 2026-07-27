using System.IO;
using Mainguard.Agents.Agents;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-06 provisioner tests on the <see cref="DualRepoFixture"/> (Linux CI leg): first-run
/// bare mirror, incremental fetch, manual-delete re-clone, and path-with-spaces/Unicode.
/// </summary>
public sealed class RepoProvisionerTests
{
    [Fact]
    public void Provision_FirstRun_CreatesHardenedBareMirror_AtSha256Path()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);

            var result = provisioner.Provision(fixture.WorkRepoPath);

            var expectedHash = RepoPathHasher.Hash(fixture.WorkRepoPath);
            Assert.Equal(expectedHash, result.RepoHash);
            Assert.Equal(Path.Combine(vmRoot, "repos", expectedHash + ".git"), result.BareRepoPath);
            Assert.True(Directory.Exists(result.BareRepoPath));

            // Bare repo.
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "rev-parse", "--is-bare-repository").Trim());
            // core.untrackedCache set from the template.
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "config", "core.untrackedCache").Trim());
            // Mirror hardened: non-FF and deletes denied (§3.4).
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "config", "receive.denyNonFastForwards").Trim());
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "config", "receive.denyDeletes").Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_SecondRun_FetchesIncrementally_NotReclone()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);
            var first = provisioner.Provision(fixture.WorkRepoPath);

            // Drop a marker inside the objects dir; a re-clone would wipe it, a fetch preserves it.
            var marker = Path.Combine(first.BareRepoPath, "objects", "mainguard-not-recloned.marker");
            File.WriteAllText(marker, "keep");

            // A new commit on the source must arrive via the incremental fetch.
            var newSha = fixture.Commit("second.txt", "second\n", "second commit");

            var second = provisioner.Provision(fixture.WorkRepoPath);

            Assert.Equal(first.BareRepoPath, second.BareRepoPath);
            Assert.True(File.Exists(marker)); // no re-clone happened
            // The fetched mirror now contains the new commit object...
            Assert.Equal("commit",
                AgentTestGit.RunChecked(second.BareRepoPath, "cat-file", "-t", newSha).Trim());
            // ...and the branch head actually advanced to it (not just objects in FETCH_HEAD).
            var headRef = AgentTestGit.RunChecked(second.BareRepoPath, "symbolic-ref", "--short", "HEAD").Trim();
            Assert.Equal(newSha, AgentTestGit.RunChecked(second.BareRepoPath, "rev-parse", headRef).Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_SourceRenamedItsDefaultBranch_FetchesTheSourcesBranch_NotTheStaleMirrorHead()
    {
        // Field bug (2026-07-22): the incremental fetch derived the branch from the MIRROR's HEAD, so
        // after the source renamed its default (master→main) the mirror still asked for `+master:master`
        // and git died "couldn't find remote ref master" — the repo copy failed. The branch must come
        // from the SOURCE, whose current HEAD branch always exists.
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);
            var first = provisioner.Provision(fixture.WorkRepoPath); // mirror HEAD tracks the seed default

            var mirrorHead = AgentTestGit.RunChecked(first.BareRepoPath, "symbolic-ref", "--short", "HEAD").Trim();

            // The source renames its default branch out from under the mirror (to a guaranteed-different
            // name, whatever `Repository.Init` seeded) and advances it. The mirror's HEAD is now stale.
            var renamed = mirrorHead == "main" ? "trunk" : "main";
            AgentTestGit.RunChecked(fixture.WorkRepoPath, "branch", "-m", mirrorHead, renamed);
            var newSha = fixture.Commit("after-rename.txt", "renamed\n", "commit on renamed default");

            // The stale-mirror-head path would throw "couldn't find remote ref <mirrorHead>" here; the
            // source-derived branch fetches cleanly.
            var second = provisioner.Provision(fixture.WorkRepoPath);

            Assert.Equal(first.BareRepoPath, second.BareRepoPath);
            // The mirror advanced the SOURCE's actual (renamed) branch to the new commit.
            Assert.Equal(newSha, AgentTestGit.RunChecked(second.BareRepoPath, "rev-parse", renamed).Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_BareRepoManuallyDeleted_ReclonesCleanly()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);
            var first = provisioner.Provision(fixture.WorkRepoPath);

            AgentTestGit.DeleteTree(first.BareRepoPath);
            Assert.False(Directory.Exists(first.BareRepoPath));

            var second = provisioner.Provision(fixture.WorkRepoPath);

            Assert.True(Directory.Exists(second.BareRepoPath));
            Assert.Equal("true", AgentTestGit.RunChecked(second.BareRepoPath, "rev-parse", "--is-bare-repository").Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_PathWithSpacesAndUnicode_HashesAndProvisionsCorrectly()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();

        // Copy the fixture work repo into a source dir whose name has spaces + Unicode.
        var spacey = Path.Combine(vmRoot, "Ünï cödé repo with spaces");
        CopyDir(fixture.WorkRepoPath, spacey);

        try
        {
            var provisioner = new RepoProvisioner(vmRoot);

            var result = provisioner.Provision(spacey);

            Assert.Equal(RepoPathHasher.Hash(spacey), result.RepoHash);
            Assert.True(Directory.Exists(result.BareRepoPath));

            // Idempotent: a second provision of the same path does not error and keeps one mirror.
            var again = provisioner.Provision(spacey);
            Assert.Equal(result.BareRepoPath, again.BareRepoPath);
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    // ---- MG-17: the mirror is bind-mounted READ-WRITE into a jail that is no longer this uid ---------

    [Fact]
    public void Provision_RecordsSharedRepositoryGroup_SoFutureGitWritesStayJailWritable()
    {
        // Platform-independent half: the config that governs every FUTURE git write inside the git dir.
        // Without it a later incremental fetch creates 0755 object fan-out directories, and the remapped
        // jail (host uid/gid 101000, sharing only the group) cannot write one object into them.
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var result = new RepoProvisioner(vmRoot).Provision(fixture.WorkRepoPath);
            Assert.Equal("group", AgentTestGit.RunChecked(result.BareRepoPath, "config", "core.sharedRepository").Trim());

            // Set on EVERY provision, not just the clone: a mirror created before the userns remap must
            // be repaired by a daemon update alone.
            AgentTestGit.RunChecked(result.BareRepoPath, "config", "core.sharedRepository", "umask");
            new RepoProvisioner(vmRoot).Provision(fixture.WorkRepoPath);
            Assert.Equal("group", AgentTestGit.RunChecked(result.BareRepoPath, "config", "core.sharedRepository").Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [LinuxOnlyFact]
    // The attribute already skips on Windows; the annotation is what tells the CA1416 analyzer so.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Provision_LeavesTheMirrorGroupWritable_AndItsDirectoriesSetgid()
    {
        // The half the config does NOT cover: the tree the clone already laid down under the daemon's
        // 022 umask. A 0755 objects/ directory is one the jail can read and never add an object to, so
        // the agent's very first commit fails — with a permission error three layers from the cause.
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var result = new RepoProvisioner(vmRoot).Provision(fixture.WorkRepoPath);

            foreach (var dir in new[] { result.BareRepoPath, Path.Combine(result.BareRepoPath, "objects") })
            {
                var mode = File.GetUnixFileMode(dir);
                Assert.True(mode.HasFlag(UnixFileMode.GroupWrite), dir + " must be group-writable");
                Assert.True(mode.HasFlag(UnixFileMode.GroupExecute), dir + " must be group-traversable");
                // setgid so anything created inside keeps the shared jail group rather than falling back
                // to the creating process's primary group.
                Assert.True(mode.HasFlag(UnixFileMode.SetGroup), dir + " must be setgid");
            }

            Assert.True(File.GetUnixFileMode(Path.Combine(result.BareRepoPath, "config")).HasFlag(UnixFileMode.GroupRead));
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }
}
