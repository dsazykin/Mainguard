using System;
using System.Linq;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// H4, client half: the verification verdict has to survive the wire, and it has to survive it
/// <b>without gaining facts nobody measured</b>.
///
/// <para>The daemon side landed three new <c>QueueEntry</c> fields — an <c>optional</c> verdict, the
/// resolved command and a timestamp — and <c>DaemonBackedOrchestrator</c> still projected
/// <c>Verification: null</c> for every entry it received. Null is this projection's own word for "never
/// verified", so a branch whose tests had just failed reached the rail claiming no verification had ever
/// happened, and the only way to learn otherwise was to pay for a second run.</para>
/// </summary>
public sealed class VerificationVerdictProjectionTests
{
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private static Proto.QueueUpdate Update(Action<Proto.QueueEntry> shape)
    {
        var entry = new Proto.QueueEntry
        {
            AgentId = "agent-a",
            State = "VerificationFailed",
            GateReason = "tests failed — node test.js",
        };
        shape(entry);
        var update = new Proto.QueueUpdate { MainSha = "abc123" };
        update.Entries.Add(entry);
        return update;
    }

    private static QueueEntry Project(Action<Proto.QueueEntry> shape)
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.ApplyQueueUpdate(Update(shape));
        return adapter.GetQueue().Single();
    }

    /// <summary>A red run reaches the client as a red run — verdict, command and time.</summary>
    [Fact]
    public void AFailedRun_ProjectsAVerdict_WithItsCommandAndTime()
    {
        var when = new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero);
        var entry = Project(e =>
        {
            e.LastVerificationPassed = false;
            e.LastVerificationCommand = "node test.js";
            e.LastVerificationAt = when.ToString("O");
        });

        var verdict = Assert.IsType<VerificationVerdict>(entry.Verification);
        Assert.False(verdict.Passed);
        Assert.Equal("node test.js", verdict.ResolvedCommand);
        Assert.Equal(when, verdict.When);
    }

    /// <summary>
    /// <b>The guard the whole optional field exists for.</b> An entry the daemon sent NO verdict for must
    /// project as "no record" — not as a failure. Proto3 defaults a bool to false, so a projection that
    /// read the value instead of its PRESENCE would turn every never-verified entry into a failed one,
    /// which is the same conflation pointing the other way.
    /// </summary>
    [Fact]
    public void AnEntryWithNoVerdictOnTheWire_ProjectsAsNoRecord_NotAsAFailure()
    {
        var absent = Project(e => e.State = "Working");
        Assert.Null(absent.Verification);

        // …and the control that keeps that assertion from passing for the wrong reason: an explicit false
        // IS a failure, and the two differ only by field presence.
        var explicitFalse = Project(e => e.LastVerificationPassed = false);
        Assert.NotNull(explicitFalse.Verification);
        Assert.False(explicitFalse.Verification!.Passed);
    }

    /// <summary>A green record projects too — the surface has to be able to state a pass's provenance,
    /// not only a failure's.</summary>
    [Fact]
    public void APassingRun_AlsoProjectsAVerdict()
    {
        var entry = Project(e =>
        {
            e.State = "Verified";
            e.LastVerificationPassed = true;
            e.LastVerificationCommand = "dotnet test";
        });

        Assert.True(entry.Verification!.Passed);
        Assert.Equal("dotnet test", entry.Verification.ResolvedCommand);
        // No timestamp on the wire is null, never a sentinel: "the daemon did not say when" and "this
        // happened at the epoch" age differently on a surface that reports how old a verdict is.
        Assert.Null(entry.Verification.When);
    }

    /// <summary>
    /// The parked-conflict facts have to survive the wire too, and land as facts rather than as a
    /// sentence. Before this, everything the daemon knew about a conflicted worktree — where it was
    /// parked, what conflicted — stopped at the daemon: one audit event and one log line, neither of which
    /// any surface reads, while the card said "…needs a human to resolve it" and named nothing.
    /// </summary>
    [Fact]
    public void AParkedConflict_ProjectsItsWorktreeAndItsConflictingFiles()
    {
        var parked = new DateTimeOffset(2026, 8, 31, 9, 15, 0, TimeSpan.Zero);
        var entry = Project(e =>
        {
            e.State = "Working";
            e.RebaseConflict = new Proto.RebaseConflict
            {
                Worktree = "/srv/mainguard/agents/9f2c/loom-2/worktree",
                MainBranch = "main",
                ParkedAt = parked.ToString("O"),
            };
            e.RebaseConflict.Paths.Add(new[] { "src/Shared.cs", "docs/repo-map/README.md" });
        });

        var conflict = Assert.IsType<QueueRebaseConflict>(entry.RebaseConflict);
        Assert.Equal("/srv/mainguard/agents/9f2c/loom-2/worktree", conflict.Worktree);
        Assert.Equal("main", conflict.MainBranch);
        Assert.Equal(new[] { "src/Shared.cs", "docs/repo-map/README.md" }, conflict.Paths);
        Assert.Equal(parked, conflict.ParkedAt);
    }

    /// <summary>
    /// The guard the message field exists for: an entry with nothing parked must project as <c>null</c>,
    /// never as an empty conflict. An empty conflict lights both conflict controls on a row with no rebase
    /// to act on — "abort rebase" on a branch that is not rebasing is a button whose entire behaviour is
    /// an error message — and renders an empty path list, which reads as "nothing conflicts".
    /// </summary>
    [Fact]
    public void AnEntryWithNoConflictOnTheWire_ProjectsAsNull_NotAsAnEmptyConflict()
    {
        Assert.Null(Project(e => e.State = "Working").RebaseConflict);

        // …and the control that keeps that from passing for the wrong reason: a conflict whose PATHS the
        // daemon could not measure is still a conflict, and must not collapse to "no conflict".
        var unmeasured = Project(e =>
        {
            e.State = "Working";
            e.RebaseConflict = new Proto.RebaseConflict { Worktree = "/srv/wt", MainBranch = "main" };
        });
        Assert.NotNull(unmeasured.RebaseConflict);
        Assert.Empty(unmeasured.RebaseConflict!.Paths);
    }

    /// <summary>
    /// <b>The fabricated-counts guard.</b> The client-side record this replaced carried
    /// <c>TestsPassed</c>/<c>TestsTotal</c>, and no wire has ever carried either: verification observes a
    /// process exit code inside the worker's jail and parses nobody's test runner. Filling them to satisfy
    /// the type would have printed an invented "58 of 58 green" into a review surface — a reviewer
    /// believing a measurement that was never taken is strictly worse than a reviewer seeing no number.
    ///
    /// <para>Asserted structurally rather than by inspecting a rendered string, because the failure mode is
    /// someone re-adding the field "for the mock" and the projection quietly finding something to put in
    /// it. The verdict type is exactly the three facts the wire carries.</para>
    /// </summary>
    [Fact]
    public void TheVerdictType_CarriesOnlyWhatTheWireCarries_AndNoTestCounts()
    {
        var properties = typeof(VerificationVerdict)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Passed", "ResolvedCommand", "When" }, properties);

        // The three names above are exactly the three QueueEntry verdict fields on the wire. If one is
        // added there, this test is the place that says the client is allowed to carry it.
        var wire = typeof(Proto.QueueEntry).GetProperties().Select(p => p.Name).ToArray();
        Assert.Contains("LastVerificationPassed", wire);
        Assert.Contains("LastVerificationCommand", wire);
        Assert.Contains("LastVerificationAt", wire);
        Assert.DoesNotContain(wire, n => n.Contains("TestsPassed", StringComparison.Ordinal)
            || n.Contains("TestsTotal", StringComparison.Ordinal));
    }

    // ---- The approved approach: the other half of a review (2026-08-31) -----
    //
    // Same shape of defect as the verdict above, one field over: the daemon knows what a human approved,
    // and until this the client's projection had nowhere to put it — so the review cockpit showed a diff
    // and nothing to compare it against. That is how a branch whose approved approach said "keep plain
    // a / b" reached a green, unflagged, mergeable review after shipping a throwing validation layer.

    /// <summary>The approval survives the wire: the plan's identity, its approach, and the worker's
    /// declaration about following it.</summary>
    [Fact]
    public void TheApprovedApproach_SurvivesTheProjection()
    {
        var entry = Project(e =>
        {
            e.ApprovedPlanId = "plan-1";
            e.ApprovedPlanTitle = "Add divide() to the calculator";
            e.ApprovedPlanApproach = "keep plain a / b and let the language semantics stand";
            e.DeviationDeclaration = "Declared";
        });

        Assert.Equal("plan-1", entry.ApprovedPlanId);
        Assert.Equal("Add divide() to the calculator", entry.ApprovedPlanTitle);
        Assert.Equal("keep plain a / b and let the language semantics stand", entry.ApprovedApproach);
        Assert.Equal("Declared", entry.DeviationDeclaration);
    }

    /// <summary>
    /// An entry the daemon sent no approval for projects as <c>null</c>, not as an empty string. The
    /// surface decides whether to draw an approval panel from this, and proto3's empty-string default
    /// would make "never approved against anything" render as "approved, with nothing written down" —
    /// a card asserting an approval a manual agent or an external PR never had.
    /// </summary>
    [Fact]
    public void AnEntryWithNoApproval_ProjectsAsNull_NotAsAnEmptyApproval()
    {
        var entry = Project(_ => { });

        Assert.Null(entry.ApprovedPlanId);
        Assert.Null(entry.ApprovedPlanTitle);
        Assert.Null(entry.ApprovedApproach);
        Assert.Null(entry.DeviationDeclaration);
    }

    /// <summary>
    /// Jail-produced text is sanitized at the projection boundary. The daemon returns the artifact's bytes
    /// verbatim — it is the only path that hands sandbox output straight to a human surface — so the client
    /// applies the same discipline <c>AgentIpcServer.Echo</c> applies before anything is logged.
    /// </summary>
    [Theory]
    // A coloured reporter's output arrives as the plain text underneath it — the sequence goes as a
    // sequence, so no "[31m" is smeared through the log.
    [InlineData("\u001B[31mFAIL\u001B[0m suite", "FAIL suite")]
    // Newlines and tabs are structure in a test log and are kept, exactly as AgentCommitMessage keeps them.
    [InlineData("line one\n\tindented", "line one\n\tindented")]
    // CRLF is one break; a bare CR (a progress bar redrawing its line) becomes one too, rather than
    // collapsing a whole run into a single unreadable line.
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    // Anything else that is a control character is visible rather than silently dropped.
    [InlineData("nul\u0000here", "nul.here")]
    // An OSC title-set, consumed whole including its BEL terminator.
    [InlineData("\u001B]0;title\u0007done", "done")]
    public void JailText_IsSanitizedForDisplay(string raw, string expected) =>
        Assert.Equal(expected, JailText.Sanitize(raw));

    /// <summary>A tail cut mid-escape-sequence is normal — the daemon truncates at a byte offset — and must
    /// not leave a bare ESC or a smear of half a sequence behind.</summary>
    [Fact]
    public void JailText_SwallowsAnEscapeSequenceTheTailCutInHalf()
    {
        Assert.Equal("ok ", JailText.Sanitize("ok \u001B[3"));
        Assert.DoesNotContain('\u001B', JailText.Sanitize("ok \u001B"));
    }
}
