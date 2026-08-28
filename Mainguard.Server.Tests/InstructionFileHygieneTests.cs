using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Mainguard's own operating-instructions file must not become part of the user's history.
///
/// <para><b>The defect, as observed in the first end-to-end coordinator run.</b> The launcher writes the
/// adapter's declared <c>instructionsFile</c> (claude-code: <c>CLAUDE.md</c>) into the WORKER's worktree
/// root, which is what makes the CLI read it unprompted. That part worked. Nothing ignored it: the jail's
/// <c>info/exclude</c> held only <c>/.claude/settings.local.json</c>, <c>git check-ignore CLAUDE.md</c>
/// answered rc=1, and every worker's <c>git status</c> showed <c>?? CLAUDE.md</c> — so a <c>git add -A</c>
/// commits Mainguard's own briefing into the user's branch. The worker in that run noticed unprompted and
/// put it in its report.</para>
///
/// <para><b>Two halves, because an exclude alone is a lie for half the repositories.</b> A git exclude
/// does not apply to a TRACKED file. Probed in a container against real git: in a repository that tracks
/// <c>CLAUDE.md</c> — this one, and every repository with project instructions — with <c>/CLAUDE.md</c>
/// present in <c>info/exclude</c>, <c>check-ignore</c> still answers rc=1, <c>status</c> reports
/// <c>M CLAUDE.md</c>, and <c>git add -A</c> stages the daemon's text OVER the user's own. So the write
/// refuses to clobber, and the exclusion covers what it did write.</para>
///
/// <para>The in-jail half — that git really agrees the file is ignored — is
/// <c>CliSettingsRoundTripDockerTests.TheInstructionsFileMainguardWrites_IsNeverCommittedIntoTheUsersRepository</c>.
/// These are the pure halves: which paths are sent, and what is written where.</para>
/// </summary>
public sealed class InstructionFileHygieneTests
{
    private const string Instructions = "# You are a Mainguard worker\n";

    private static readonly AdapterSettingsPath WorkspaceSettings =
        new("workspace", ".probe/settings.local.json");

    private static InstalledAdapterMarker Marker(string? instructionsFile) =>
        new("probe-cli", "1.0.0", new[] { "/bin/true" },
            SettingsPaths: new[] { WorkspaceSettings, new AdapterSettingsPath("home", ".probe/settings.json") },
            InstructionsFile: instructionsFile);

    // ---- what the jail is told to ignore ---------------------------------------------------------

    [Fact]
    public void TheIgnoreList_CarriesTheInstructionsFileAsWellAsTheSettingsPath()
    {
        Assert.Equal(
            new[] { WorkspaceSettings.Path, "CLAUDE.md" },
            SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths(Marker("CLAUDE.md")));
    }

    /// <summary>
    /// The exclusion follows the DECLARED name, not <c>CLAUDE.md</c>. This is the guard against the
    /// obvious wrong fix: hardcoding today's filename keeps every test green and silently stops covering
    /// the next CLI Mainguard ships — the same "a description that outlived the thing it described"
    /// failure (MG-12) this branch keeps finding.
    /// </summary>
    [Fact]
    public void TheIgnoredNameIsWhicheverTheAdapterDeclares_NotClaudesOne()
    {
        Assert.Contains("AGENTS.md", SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths(Marker("AGENTS.md")));
        Assert.DoesNotContain("CLAUDE.md", SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths(Marker("AGENTS.md")));
    }

    [Fact]
    public void AnAdapterThatDeclaresNoInstructionsFile_AddsNothingToTheIgnoreList()
    {
        Assert.Equal(
            new[] { WorkspaceSettings.Path },
            SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths(Marker(null)));
    }

    /// <summary>
    /// A marker is a JSON file on disk that no manifest parse re-validates, and this list is written
    /// verbatim into a git ignore file. A rooted or <c>..</c>-bearing name must never reach it.
    /// </summary>
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../escape.md")]
    [InlineData("~/CLAUDE.md")]
    [InlineData("")]
    public void AMalformedDeclaredName_NeverBecomesAnIgnoreEntry(string declared)
    {
        Assert.Equal(
            new[] { WorkspaceSettings.Path },
            SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths(Marker(declared)));
    }

    // ---- what is actually written into the worktree ----------------------------------------------

    [Fact]
    public void IntoAnEmptySlot_TheInstructionsAreStaged()
    {
        using var worktree = new TempDir();

        var written = Launcher().TryStageInstructionsFile(worktree.Path, Marker("CLAUDE.md"), Instructions, "a1");

        Assert.Equal("CLAUDE.md", written);
        Assert.Equal(Instructions, File.ReadAllText(Path.Combine(worktree.Path, "CLAUDE.md")));
    }

    /// <summary>
    /// The case an exclude cannot save: the repository already has a <c>CLAUDE.md</c> of its own, so it is
    /// TRACKED, so <c>info/exclude</c> is inert for it and a write is an ordinary modification that
    /// <c>git add -A</c> stages. Overwriting would replace the user's project instructions with
    /// Mainguard's — worse than the untracked-file defect this change is about, and silent.
    /// </summary>
    [Fact]
    public void OverAFileTheUserAlreadyHas_NothingIsWritten()
    {
        using var worktree = new TempDir();
        var theirs = "# The user's own project instructions\n";
        File.WriteAllText(Path.Combine(worktree.Path, "CLAUDE.md"), theirs);

        var written = Launcher().TryStageInstructionsFile(worktree.Path, Marker("CLAUDE.md"), Instructions, "a1");

        Assert.Null(written);
        Assert.Equal(theirs, File.ReadAllText(Path.Combine(worktree.Path, "CLAUDE.md")));
    }

    /// <summary>
    /// <c>Path.Combine(worktree, "../../x")</c> writes OUTSIDE the worktree. The manifest parser refuses
    /// such a declaration now, but a marker never goes back through it, so the launcher re-asks.
    /// </summary>
    [Theory]
    [InlineData("../escape.md")]
    [InlineData("~/escape.md")]
    public void AnEscapingDeclaredName_WritesNothingAnywhere(string declared)
    {
        using var parent = new TempDir();
        var worktree = Path.Combine(parent.Path, "wt");
        Directory.CreateDirectory(worktree);

        var written = Launcher().TryStageInstructionsFile(worktree, Marker(declared), Instructions, "a1");

        Assert.Null(written);
        Assert.Empty(Directory.GetFiles(parent.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AnAdapterThatDeclaresNoInstructionsFile_WritesNothing()
    {
        using var worktree = new TempDir();

        Assert.Null(Launcher().TryStageInstructionsFile(worktree.Path, Marker(null), Instructions, "a1"));
        Assert.Empty(Directory.GetFiles(worktree.Path));
    }

    /// <summary>
    /// Everything the staging step writes must be a name the ignore step also emits. Stated as one
    /// assertion rather than as two independently-true facts, because the defect was precisely that the
    /// two sides disagreed about the same file.
    /// </summary>
    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("AGENTS.md")]
    [InlineData(".config/GEMINI.md")]
    public void WhateverIsStaged_IsAlsoIgnored(string declared)
    {
        using var worktree = new TempDir();
        var marker = Marker(declared);

        var written = Launcher().TryStageInstructionsFile(worktree.Path, marker, Instructions, "a1");

        Assert.NotNull(written);
        Assert.Contains(written!, SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths(marker));
    }

    private static SandboxAgentLauncher Launcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "mg-instr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return new SandboxAgentLauncher(
            new AgentSessionRepoScopingTests.FakeAgentEnvironment(
                root, new AgentSessionRepoScopingTests.RecordingEngine()));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mg-instr-wt-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* never fail a test from cleanup */ }
        }
    }
}
