using System;
using System.IO;
using System.Linq;
using System.Text;
using Mainguard.Agents.UI.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The host-side half of the settings round trip: the per-repository store the owner's approvals live
/// in between agents.
///
/// <para><b>The scope decision, made testable.</b> The owner asked for "a global Mainguard .claude or
/// per repo .claude" and this is the per-repo answer, because a permission allowlist is a standing
/// grant of execution: approving something while working on one repository must not silently
/// pre-approve it in another, where the same command name can mean something entirely different. That
/// is a property of this class, so it is asserted here rather than described in a comment.</para>
///
/// <para>Everything runs against a temp root — no test touches the user's real store, and none of this
/// goes near the OS keychain, which stays credentials-only.</para>
/// </summary>
public class CliSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mg-cli-settings-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private static CliSettingsFileEntry Grant(string command) =>
        new("workspace", ".claude/settings.local.json",
            Encoding.UTF8.GetBytes("{\"permissions\":{\"allow\":[\"" + command + "\"]}}"));

    private CliSettingsStore NewStore() => new(_root);

    [Fact]
    public void WhatWasApprovedInOneAgent_IsThereForTheNext()
    {
        var store = NewStore();
        var grant = Grant("Bash(npm test:*)");

        Assert.True(store.Save("repo-a", "claude-code", new[] { grant }));

        var loaded = Assert.Single(store.Load("repo-a", "claude-code"));
        Assert.Equal(grant.Root, loaded.Root);
        Assert.Equal(grant.Path, loaded.Path);
        Assert.Equal(grant.Content, loaded.Content);
    }

    [Fact]
    public void ApprovingSomethingInOneRepository_DoesNotApproveItInAnother()
    {
        var store = NewStore();
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(terraform apply:*)") });

        // The whole scope decision in one assertion. A shared store would answer repo-b with repo-a's
        // allowlist, and `terraform apply` would already be approved in a repository the user has never
        // approved anything in.
        Assert.Empty(store.Load("repo-b", "claude-code"));
    }

    [Fact]
    public void OneClisSettings_AreNotAnothersEvenInTheSameRepository()
    {
        var store = NewStore();
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(ls:*)") });

        Assert.Empty(store.Load("repo-a", "codex"));
    }

    [Fact]
    public void ABlankScope_IsNeverAWildcard()
    {
        var store = NewStore();
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(ls:*)") });

        // A missing repo handle must not collapse into "some shared bucket" — that is the MG-6 defect
        // in a different store.
        Assert.Empty(store.Load(string.Empty, "claude-code"));
        Assert.False(store.Save(string.Empty, "claude-code", new[] { Grant("Bash(ls:*)") }));
    }

    [Fact]
    public void ALaterHarvest_ReplacesThatFile_AndLeavesTheOthersAlone()
    {
        var store = NewStore();
        var home = new CliSettingsFileEntry("home", ".claude/settings.json", Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}"));
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(ls:*)"), home });

        // A session in which the CLI rewrote only the workspace file. The home entry must survive: a
        // file one session did not recreate must never erase a working setting.
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(ls:*) Bash(git status:*)") });

        var loaded = store.Load("repo-a", "claude-code");
        Assert.Equal(2, loaded.Count);
        Assert.Equal(
            "{\"theme\":\"dark\"}",
            Encoding.UTF8.GetString(loaded.Single(e => e.Root == "home").Content));
        Assert.Contains(
            "git status",
            Encoding.UTF8.GetString(loaded.Single(e => e.Root == "workspace").Content));
    }

    [Fact]
    public void AnEmptyHarvest_WritesNothing_AndNeverClearsWhatIsStored()
    {
        var store = NewStore();
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(ls:*)") });

        Assert.False(store.Save("repo-a", "claude-code", Array.Empty<CliSettingsFileEntry>()));
        Assert.Single(store.Load("repo-a", "claude-code"));
    }

    [Fact]
    public void ACorruptStoreFile_MeansNoSettings_NotACrash()
    {
        var store = NewStore();
        var path = store.FilePathFor("repo-a", "claude-code");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not the file we wrote");

        // The worst case is the pre-feature behaviour (the CLI asks again), never a failed spawn.
        Assert.Empty(store.Load("repo-a", "claude-code"));
    }

    [Fact]
    public void TheStoreIsAPlainFileTheOwnerCanFindAndDelete()
    {
        var store = NewStore();
        store.Save("repo-a", "claude-code", new[] { Grant("Bash(ls:*)") });

        var path = store.FilePathFor("repo-a", "claude-code");
        Assert.True(File.Exists(path));
        Assert.Contains(CliSettingsStore.DirectoryName, path);
        // Readable JSON rather than an opaque blob — "which commands am I carrying forward" has to be
        // answerable without running Mainguard, and "forget them" has to be a delete.
        Assert.Contains("settings.local.json", File.ReadAllText(path));

        File.Delete(path);
        Assert.Empty(store.Load("repo-a", "claude-code"));
    }

    [Fact]
    public void TwoScopesThatAreNotFilenameSafe_StillGetDifferentFiles()
    {
        var store = NewStore();

        // Repo handles are opaque, and both of these would sanitise to the same thing under a naive
        // replace — which would quietly merge two repositories' allowlists into one.
        var first = store.FilePathFor("a/b", "claude-code");
        var second = store.FilePathFor("a\\b", "claude-code");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("..", first);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* never fail a test from cleanup */ }
    }
}
