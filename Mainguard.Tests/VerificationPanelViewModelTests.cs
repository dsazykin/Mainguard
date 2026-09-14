using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <see cref="VerificationPanelViewModel"/> — the panel both merge surfaces render an entry's verification
/// verdict through, and the one that had no tests at all.
///
/// <para>Everything here is a claim a human reads before deciding to merge, so each test names the WRONG
/// sentence it prevents rather than the property it sets. The three that matter: a failed run must never
/// render as an un-run one; a verdict whose branch has moved must not read as current; and reading a log
/// must never be confused with re-running the suite.</para>
/// </summary>
public sealed class VerificationPanelViewModelTests
{
    private const string AgentId = "agent-a";

    private static QueueEntry Entry(
        WorkerMergeState state, VerificationVerdict? verdict) =>
        new(AgentId, AgentId, $"agent/{AgentId}", state, "",
            Verification: verdict, FlaggedItems: Array.Empty<FlaggedItem>());

    // ---- the three-way outcome ------------------------------------------

    /// <summary>
    /// A RED verdict is red, and says so.
    ///
    /// <para>This is the defect the panel exists for: the projection collapsed "the tests failed" into
    /// "not verified yet", so a branch whose suite had just gone red asked to be merged with the same
    /// sentence as one that had never been tested — and the only route to the truth was a second, paid,
    /// verification run.</para>
    /// </summary>
    [Fact]
    public void AFailedRun_ReadsAsFailed_NotAsNeverVerified()
    {
        var vm = new VerificationPanelViewModel(AgentId, new FakeQueue());

        vm.Update(Entry(WorkerMergeState.VerificationFailed,
            new VerificationVerdict(Passed: false, "node test.js", DateTimeOffset.UtcNow)));

        Assert.True(vm.HasRecord);
        Assert.True(vm.IsFailed);
        Assert.False(vm.IsPassed);
        Assert.StartsWith("Tests failed", vm.FactsText, StringComparison.Ordinal);
        Assert.DoesNotContain("Not verified yet", vm.FactsText, StringComparison.Ordinal);
    }

    /// <summary>Never-run is its own state, and is the only one with nothing to read. It must not borrow
    /// a pass by omission — hence <c>IsPassed</c> being its own flag rather than <c>!IsFailed</c>.</summary>
    [Fact]
    public void NoRecord_ReadsAsNeverVerified_AndIsNeitherPassNorFail()
    {
        var vm = new VerificationPanelViewModel(AgentId, new FakeQueue());

        vm.Update(Entry(WorkerMergeState.Working, verdict: null));

        Assert.False(vm.HasRecord);
        Assert.False(vm.IsPassed);
        Assert.False(vm.IsFailed);
        Assert.False(vm.IsStale);            // "no run, and it's out of date" is not a fact about anything
        Assert.Contains("Not verified yet", vm.FactsText, StringComparison.Ordinal);
    }

    /// <summary>The command that produced a green is provenance, not decoration: a branch that rewrote its
    /// own test command produces a green that means nothing, so the reviewer has to see WHAT ran.</summary>
    [Fact]
    public void APassedRun_NamesTheCommandThatDecidedIt()
    {
        var vm = new VerificationPanelViewModel(AgentId, new FakeQueue());

        vm.Update(Entry(WorkerMergeState.Verified,
            new VerificationVerdict(Passed: true, "dotnet test", DateTimeOffset.UtcNow)));

        Assert.True(vm.IsPassed);
        Assert.StartsWith("Tests passed", vm.FactsText, StringComparison.Ordinal);
        Assert.Contains("dotnet test", vm.FactsText, StringComparison.Ordinal);
    }

    // ---- staleness -------------------------------------------------------

    /// <summary>
    /// A green whose branch has moved out from under it is qualified IN the verdict clause.
    ///
    /// <para>The observed defect: an entry whose keep-alive rebase had conflicted rendered
    /// "Tests passed · node test.js · &lt;time&gt;" immediately above "rebasing this branch onto the new
    /// main hit a conflict". Both were true of different moments and the pass read as the answer. The
    /// qualifier is therefore part of the sentence, not a footnote after it — a reader who stops at the
    /// first three words has to have been told already.</para>
    /// </summary>
    [Theory]
    [InlineData(WorkerMergeState.Working)]        // the agent pushed new commits, or a rebase parked it
    [InlineData(WorkerMergeState.StaleVerified)]  // that fact by name
    [InlineData(WorkerMergeState.Verifying)]      // a NEWER run is under way; this verdict is the old one
    public void AVerdictWhoseBranchMoved_IsMarkedStaleInTheSentenceItself(WorkerMergeState state)
    {
        var vm = new VerificationPanelViewModel(AgentId, new FakeQueue());

        vm.Update(Entry(state, new VerificationVerdict(Passed: true, "node test.js", DateTimeOffset.UtcNow)));

        Assert.True(vm.IsStale);
        Assert.Contains("stale", vm.FactsText, StringComparison.Ordinal);
        // The qualifier lands before the provenance, i.e. inside the verdict clause.
        Assert.True(
            vm.FactsText.IndexOf("stale", StringComparison.Ordinal)
                < vm.FactsText.IndexOf("node test.js", StringComparison.Ordinal),
            $"the stale qualifier must be part of the verdict clause, not appended after it: {vm.FactsText}");
    }

    /// <summary>The terminal records are NOT stale: nothing moved under them, and they are the permanent
    /// account of what was true when the entry left the queue.</summary>
    [Theory]
    [InlineData(WorkerMergeState.Merged)]
    [InlineData(WorkerMergeState.Rejected)]
    [InlineData(WorkerMergeState.Verified)]
    [InlineData(WorkerMergeState.AwaitingReview)]
    public void AVerdictThatStillDescribesTheBranch_IsNotMarkedStale(WorkerMergeState state)
    {
        var vm = new VerificationPanelViewModel(AgentId, new FakeQueue());

        vm.Update(Entry(state, new VerificationVerdict(Passed: true, "node test.js", DateTimeOffset.UtcNow)));

        Assert.False(vm.IsStale);
    }

    // ---- the log: fetched on demand, never a re-run ----------------------

    /// <summary>
    /// Expanding reads; it never re-runs.
    ///
    /// <para>That distinction is the point of the whole feature — a re-run costs minutes of real jail time
    /// and can answer differently, so it must never be the price of finding out why something failed.</para>
    /// </summary>
    [Fact]
    public async Task Expanding_ReadsTheLog_AndNeverRunsVerification()
    {
        var queue = new FakeQueue { Log = Log("58 assertions, 1 failed") };
        var vm = new VerificationPanelViewModel(AgentId, queue);
        vm.Update(Entry(WorkerMergeState.VerificationFailed,
            new VerificationVerdict(false, "node test.js", DateTimeOffset.UtcNow)));

        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.True(vm.IsExpanded);
        Assert.Equal("58 assertions, 1 failed", vm.LogText);
        Assert.Equal(1, queue.LogFetches);
        Assert.Equal(0, queue.VerificationRuns);
    }

    /// <summary>The rail re-projects on every daemon event, so the log is cached: collapsing and
    /// re-expanding the SAME verdict must not be another file read on the daemon.</summary>
    [Fact]
    public async Task ReExpandingTheSameVerdict_DoesNotRefetch()
    {
        var queue = new FakeQueue { Log = Log("output") };
        var vm = new VerificationPanelViewModel(AgentId, queue);
        var verdict = new VerificationVerdict(false, "node test.js", DateTimeOffset.UtcNow);
        vm.Update(Entry(WorkerMergeState.VerificationFailed, verdict));

        await vm.ToggleCommand.ExecuteAsync(null);   // open  (fetch)
        await vm.ToggleCommand.ExecuteAsync(null);   // close
        await vm.ToggleCommand.ExecuteAsync(null);   // open  (cached)

        Assert.Equal(1, queue.LogFetches);
        Assert.Equal("output", vm.LogText);
    }

    /// <summary>
    /// A NEW verdict drops the cached log rather than showing it underneath.
    ///
    /// <para>Showing a previous run's output under a newer verdict is the same class of lie the panel
    /// exists to remove: the text would describe code the branch no longer has, under a headline about
    /// code it does.</para>
    /// </summary>
    [Fact]
    public async Task ANewVerdict_DropsThePreviousRunsOutput()
    {
        var queue = new FakeQueue { Log = Log("the first run") };
        var vm = new VerificationPanelViewModel(AgentId, queue);
        vm.Update(Entry(WorkerMergeState.VerificationFailed,
            new VerificationVerdict(false, "node test.js", DateTimeOffset.UtcNow)));
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("the first run", vm.LogText);

        vm.Update(Entry(WorkerMergeState.Verified,
            new VerificationVerdict(true, "node test.js", DateTimeOffset.UtcNow.AddMinutes(1))));

        Assert.Equal("", vm.LogText);
        Assert.False(vm.IsExpanded);

        queue.Log = Log("the second run");
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("the second run", vm.LogText);
        Assert.Equal(2, queue.LogFetches);
    }

    /// <summary>A fetch that fails is REPORTED. An empty expander is indistinguishable from a suite that
    /// printed nothing, which is a different fact about the run.</summary>
    [Fact]
    public async Task AFetchThatFails_SaysSo_RatherThanShowingAnEmptyBox()
    {
        var queue = new FakeQueue { LogFailure = new InvalidOperationException("the daemon is not reachable") };
        var vm = new VerificationPanelViewModel(AgentId, queue);
        vm.Update(Entry(WorkerMergeState.VerificationFailed,
            new VerificationVerdict(false, "node test.js", DateTimeOffset.UtcNow)));

        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal("", vm.LogText);
        Assert.Contains("the daemon is not reachable", vm.LogNotice, StringComparison.Ordinal);
        Assert.False(vm.IsLoading);
    }

    /// <summary>The four things the surface must say ABOUT a log rather than in it, kept apart. Each is a
    /// different fact and none may render as any of the others — in particular a deleted artifact must not
    /// render as a suite that printed nothing.</summary>
    [Theory]
    [InlineData(true, "", "", false, "The run produced no output.")]
    [InlineData(true, "line 1", "", true, "Showing the end of a longer log")]
    [InlineData(true, "", "the log file was pruned", false, "The log file was pruned.")]
    [InlineData(false, "", "", false, "The daemon has no verification record")]
    public async Task TheLogNotice_KeepsTheFourAnswersApart(
        bool hasRecord, string text, string unavailable, bool truncated, string expected)
    {
        var queue = new FakeQueue
        {
            Log = new VerificationLog(hasRecord, false, "node test.js", "abc123",
                DateTimeOffset.UtcNow, text, truncated, unavailable),
        };
        var vm = new VerificationPanelViewModel(AgentId, queue);
        vm.Update(Entry(WorkerMergeState.VerificationFailed,
            new VerificationVerdict(false, "node test.js", DateTimeOffset.UtcNow)));

        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.Contains(expected, vm.LogNotice, StringComparison.Ordinal);
    }

    /// <summary>The toggle's own label is state, so a human never presses "Show test output" twice while a
    /// fetch is running.</summary>
    [Fact]
    public void ToggleLabel_TracksTheState()
    {
        var vm = new VerificationPanelViewModel(AgentId, new FakeQueue());
        Assert.Equal("Show test output", vm.ToggleButtonText);

        vm.IsExpanded = true;
        Assert.Equal("Hide test output", vm.ToggleButtonText);

        vm.IsLoading = true;
        Assert.Equal("Loading…", vm.ToggleButtonText);
    }

    private static VerificationLog Log(string text) =>
        new(HasRecord: true, Passed: false, "node test.js", "abc123", DateTimeOffset.UtcNow,
            text, Truncated: false, UnavailableReason: "");

    /// <summary>Counts what the panel asks the daemon for, so "reading is not re-running" is asserted
    /// rather than assumed.</summary>
    private sealed class FakeQueue : IMergeQueueService
    {
        public VerificationLog Log { get; set; } =
            new(false, false, "", "", null, "", false, "");

        public Exception? LogFailure { get; set; }

        public int LogFetches { get; private set; }

        public int VerificationRuns { get; private set; }

        public Task<VerificationLog> GetVerificationLogAsync(string agentId)
        {
            LogFetches++;
            return LogFailure is { } ex
                ? Task.FromException<VerificationLog>(ex)
                : Task.FromResult(Log);
        }

        public Task<VerificationOutcome> RunVerificationAsync(string agentId)
        {
            VerificationRuns++;
            return Task.FromResult(new VerificationOutcome(true, true, ""));
        }

        // ---- the rest of the seam: never exercised by this panel ----
        public IReadOnlyList<QueueEntry> GetQueue() => Array.Empty<QueueEntry>();
        public string MainSha => "";
        public DateTimeOffset? MirrorMainRefreshedAt => null;
        public string? MirrorMainRefreshError => null;
        public Task RefreshMirrorMainAsync() => Task.CompletedTask;
        public bool CanMerge(string agentId, out string reason) { reason = ""; return false; }
        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) =>
            throw new NotSupportedException();
        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();
        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();
        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) =>
            throw new NotSupportedException();
        public Task ResolveConflictWithAgentAsync(string agentId) => throw new NotSupportedException();
        public Task AbortRebaseAsync(string agentId) => throw new NotSupportedException();
        public Task ClearStalledVerificationAsync(string agentId) => throw new NotSupportedException();
    }
}
