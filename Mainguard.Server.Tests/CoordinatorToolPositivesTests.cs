using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The half of the four-tool surface nothing proved: that <c>send_worker_prompt</c> and
/// <c>request_verification</c> can SUCCEED.
///
/// <para><b>Why this file exists.</b> <see cref="CoordinatorRoleLockTests"/> is 29 tests and every
/// assertion in it about either op is a refusal — not one <c>Assert.True</c> near either. A handler that
/// failed both unconditionally would pass that entire suite green. Its own paired positive was written
/// against exactly this risk ("without this, every Assert.False above would pass on a handler that
/// refused everything") and covers <c>get_worker_status</c> only, so the reasoning was right and the
/// coverage stopped one tool short. Contract §8's live four-tool run has also not been performed
/// (phase 3 §4), so until this file there was no evidence anywhere — automated or manual — that two of
/// the contract's four tools do anything at all.</para>
///
/// <para><b>What makes a positive possible here.</b> A prompt is delivered by writing to the worker's
/// bound pty, and the plan-gate rig's substrate has none — so the op could previously only be watched
/// failing. Binding a real <see cref="BoundTerminalSession"/> over a double gives the delivery path
/// somewhere to land.</para>
///
/// <para><b>What the assertions are on, after the 2026-08-30 correction.</b> They used to be on the
/// BYTES that arrived — <c>Assert.Equal("prefer the stdlib\n", written)</c> — written to prove delivery
/// rather than a return value, which was the right instinct aimed at the wrong side of the boundary. A
/// PTY-attached CLI submits on <b>CR</b>; <c>"\n"</c> types a newline into its input box and presses
/// nothing. That assertion was therefore green over a tool that had never once worked: in a live run
/// three prompts to two workers sat unsubmitted and ACCUMULATED — one worker's box held two
/// concatenated prompts — while the daemon logged "prompt delivered" for each. The assertions are now
/// on what <see cref="RawModeCliDouble"/> received as a <i>submitted line</i> and on what was left
/// sitting in its input box: the two facts a byte log cannot tell apart.</para>
/// </summary>
public sealed class CoordinatorToolPositivesTests : PlanGateIpcTestBase, IClassFixture<PlanGateRig>
{
    /// <summary>
    /// A steer of the length a coordinator actually sends — the 139-byte message from the live run that
    /// exposed defect J2. The steering tests below use THIS rather than a three-word literal, and that is
    /// not cosmetic: <c>body + CR</c> in one write submits `go` and does <b>not</b> submit this, so a
    /// suite built on short strings passed for a channel that was inert for every real message. Length is
    /// the variable the defect lives on, so the fixture has to carry it.
    /// </summary>
    private const string RealisticSteer =
        "Add one more assertion to test.js covering the empty-input case, then re-run the suite and "
        + "record the result in your mainguard-plan commit.";

    private const string SecondRealisticSteer =
        "Now re-read the acceptance criteria in the plan, confirm the suite is green, and record the "
        + "exact pass and fail counts before you request verification.";

    public CoordinatorToolPositivesTests(PlanGateRig rig) : base(rig)
    {
    }

    /// <summary>
    /// An approved worker with a live CLI receives the prompt <b>as a submitted line</b> — the CLI's own
    /// input box is empty afterwards. That second half is the assertion: bytes reaching the pty is what
    /// the shipped defect already achieved.
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_ReachesAnApprovedWorkersCli_AsASubmittedLine()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("tidy the retry helper");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: RealisticSteer));

        Assert.True(response.Ok, $"an approved worker with a live CLI refused a prompt: {response.Error}");

        var submitted = await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { RealisticSteer }, submitted);

        // The discriminator. Under the shipped `prompt + "\n"` the bytes still ARRIVE — they just stay
        // here, in the input box, forever.
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// The live failure, as a test: two steers in a row must land as <b>two</b> submitted lines, not as
    /// one input box holding both. This is what a stress run actually produced — a worker whose input
    /// line held two concatenated prompts and whose transcript showed no turn for either.
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_Twice_LandsAsTwoLines_NotOneAccumulatedInputBox()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("two steers");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        foreach (var text in new[] { RealisticSteer, SecondRealisticSteer })
        {
            var response = await CallAsync(coordinatorId, new AgentIpcRequest(
                AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: text));
            Assert.True(response.Ok, $"a steer was refused: {response.Error}");
        }

        var submitted = await cli.WaitForSubmittedAsync(2, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { RealisticSteer, SecondRealisticSteer }, submitted);
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// A multi-line steer arrives as ONE submitted line carrying its newlines — and a stray CR inside the
    /// text does not cut it into two turns. Measured against the real CLI: an embedded CR submits the
    /// prefix and strands the remainder, so a message authored with CRLF would silently steer a worker
    /// with half a sentence.
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_WithEmbeddedNewlines_SubmitsExactlyOnce()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("multi-line steer");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp,
            AgentId: workerId,
            Prompt: RealisticSteer + "\r\n" + SecondRealisticSteer + "\n"));

        Assert.True(response.Ok, $"a multi-line steer was refused: {response.Error}");

        var submitted = await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { RealisticSteer + "\n" + SecondRealisticSteer }, submitted);
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// The observation the coordinator is given, in both readings. A CLI that repaints is reported as
    /// having done so; a CLI that stays silent is reported as having produced nothing — still <c>Ok</c>,
    /// because the daemon really did press Enter, but no longer indistinguishable from it. The old
    /// response said "PromptSent" either way, and the shim printed the worker id instead of the status,
    /// so nothing about a steer's fate reached the caller at all.
    /// </summary>
    [Theory]
    [InlineData(true, "redrew")]
    [InlineData(false, "produced no output")]
    public async Task SendWorkerPrompt_ReportsWhetherTheCliWasSeenReacting(bool redraws, string expected)
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync($"reaction {redraws}");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble(redraws);
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: RealisticSteer));

        Assert.True(response.Ok, $"a steer was refused: {response.Error}");
        Assert.Contains(expected, response.Status ?? string.Empty, StringComparison.Ordinal);

        // Either way the line WAS submitted — the reaction is an observation about the CLI, not a
        // second opinion about whether Enter was pressed.
        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));

        // And the caller can actually see it: the shim prints `status` only when no agentId is present.
        Assert.Null(response.AgentId);
    }

    /// <summary>
    /// <b>Defect J2, as a test at the length that breaks it.</b> A steer of realistic size is submitted —
    /// and the discriminator is that the CLI's input box is EMPTY afterwards.
    ///
    /// <para>Why this is not a duplicate of the first test: this one pins the mechanism. The double
    /// models what a real TUI does — a CR arriving in the same read as a substantial body is pasted
    /// content, not Enter — so a regression that appends the terminator to the message leaves the whole
    /// steer sitting in <see cref="RawModeCliDouble.PendingInput"/> with a literal newline where the CR
    /// was, which is exactly what the live run's transcript showed
    /// (<c>'…mainguard-plan commit.\rgo'</c>). Nothing shorter than this catches it: the identical code
    /// path submits a 3-byte poke correctly.</para>
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_AtRealisticLength_Submits_NotAccumulatesInTheInputBox()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("a realistic steer");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        // The length is the test. A poke through this same path always worked.
        Assert.True(RealisticSteer.Length > 100);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: RealisticSteer));

        Assert.True(response.Ok, $"a realistic steer was refused: {response.Error}");
        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));

        // The J2 signature: under body+CR-in-one-write the text lands here instead, un-submitted, and
        // accumulates with every further steer.
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// <b>Defect J3: the status must not assert what the daemon cannot know.</b>
    ///
    /// <para>It used to end "Enter was pressed and its CLI redrew in response." A redraw cannot carry
    /// that — it fires on the CLI's own echo, and a CLI already mid-turn repaints without having read
    /// anything — but it read as confirmation. In a live run the same sentence came back for six prompts
    /// and the coordinator had to reason its way out of trusting it: "prompt confirms keystrokes landed,
    /// not that the worker accepted anything." A tool whose success message an agent must discount is a
    /// broken tool, so the limit is stated in the message itself.</para>
    ///
    /// <para>Asserted in <b>both</b> readings, because the failure being guarded against is a confident
    /// sentence, and the confident sentence was the one on the happy path.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendWorkerPrompt_ReportsWhatWasObserved_WithoutClaimingTheWorkerAccepted(bool redraws)
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync($"honest status {redraws}");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble(redraws);
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: RealisticSteer));

        Assert.True(response.Ok, $"a steer was refused: {response.Error}");
        var status = response.Status ?? string.Empty;

        // It says what was DONE, and marks the observation as an observation.
        Assert.Contains("pressed Enter as a separate keystroke", status, StringComparison.Ordinal);
        Assert.Contains("Observed:", status, StringComparison.Ordinal);

        // And it states the limit outright, so no reader has to derive it.
        Assert.Contains("NOT confirmation", status, StringComparison.Ordinal);
        Assert.Contains($"Only {workerId} itself can confirm", status, StringComparison.Ordinal);

        // A second prompt is a second turn: the status must never invite a retry.
        Assert.Contains("not a retry", status, StringComparison.Ordinal);
        Assert.DoesNotContain("retry the prompt", status, StringComparison.OrdinalIgnoreCase);

        // The sentence the defect was: a redraw asserted as proof that the prompt landed.
        Assert.DoesNotContain("redrew in response", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative that gives the positive above its meaning: with no bound CLI the same call is
    /// refused, and refused with the sentence a human can act on rather than a raw errno — the defect
    /// that <c>TrySendPromptAsync</c> letting <see cref="IOException"/> escape used to produce.
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_WithNoLiveCli_IsRefusedInWordsNotAnErrno()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("no cli here");
        await ApproveAsync(workerId);

        // A spawn binds a terminal, so "no live CLI" has to be arranged rather than assumed — releasing
        // it is what a jail dying looks like to this code path, which is the case the refusal is for.
        Rig.Terminals.Release(KeyFor(workerId));
        Assert.Null(Rig.Terminals.TryGetBound(KeyFor(workerId)));

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "steer"));

        Assert.False(
            response.Ok,
            $"a worker with no bound CLI accepted a prompt (status={response.Status}, worker={workerId})");
        Assert.Contains("no live CLI to steer", response.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Input/output error", response.Error ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- the frozen jail: a delivery that succeeds and means nothing ----------------------------

    /// <summary>
    /// <b>A prompt at a PAUSED worker is refused, and nothing reaches its CLI.</b>
    ///
    /// <para>The shipped hole: when a merge's auto-rebase conflicts the daemon <c>docker pause</c>s the
    /// worker's jail and leaves it frozen for a human to resolve — and <c>send_worker_prompt</c> kept
    /// answering <c>Ok</c>. Every other guard is about whether the coordinator MAY steer; none of them
    /// asks whether anything on the other end can still read. The bytes were typed into a channel inside
    /// a SIGSTOPped process, the tool reported success, and the coordinator polled a worker that could
    /// never answer.</para>
    ///
    /// <para>Both frozen spellings are exercised: <c>Paused</c> is what the reconciler's drift pass and
    /// the human pause write, <c>Conflict</c> is what the keep-alive rebase writes seconds earlier — and
    /// the jail is frozen for both. The control at the top is what stops this passing on a handler that
    /// refused every prompt: the SAME worker, over the SAME socket, accepts one first.</para>
    /// </summary>
    [Theory]
    [InlineData(AgentSessionReconciler.PausedState)]
    [InlineData("Conflict")]
    public async Task SendWorkerPrompt_ToAFrozenJail_IsRefused_AndNothingIsTyped(string frozenState)
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync($"frozen {frozenState}");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        // The control: while the jail is running the very same call lands.
        var accepted = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: RealisticSteer));
        Assert.True(accepted.Ok, $"the control steer was refused: {accepted.Error}");
        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));

        // …and now the jail is frozen, exactly as a conflicted keep-alive rebase leaves it.
        Rig.Sessions.MarkState(KeyFor(workerId), frozenState, "the keep-alive rebase conflicted");

        var refused = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: SecondRealisticSteer));

        Assert.False(
            refused.Ok,
            $"a {frozenState} worker accepted a prompt (status={refused.Status}, worker={workerId})");

        var error = refused.Error ?? string.Empty;
        // It names the state, says nothing was sent, and says who has to act — the three things a
        // coordinator needs to stop the polling loop the defect produced.
        Assert.Contains($"{workerId} is paused", error, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", error, StringComparison.Ordinal);
        Assert.Contains("human", error, StringComparison.Ordinal);
        Assert.Contains("do not keep polling", error, StringComparison.Ordinal);

        // The assertion that makes it a guard rather than a message: the second steer never reached the
        // CLI at all — not as a submitted line, and not sitting in its input box either.
        await Task.Delay(200);
        Assert.Equal(new[] { RealisticSteer }, cli.SubmittedLines);
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// The same hole on <c>request_verification</c>, which runs the test command inside that same frozen
    /// jail. Refused BEFORE the merge-queue step — and the neighbouring positive
    /// (<see cref="RequestVerification_ForAnApprovedOwnedWorker_ReachesTheQueueStep"/>) is what proves the
    /// refusal comes from the pause and not from the op being inert; the control here re-establishes it on
    /// the same worker.
    /// </summary>
    [Fact]
    public async Task RequestVerification_ForAFrozenJail_IsRefused_BeforeTheQueueStep()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("verify a frozen jail");
        await ApproveAsync(workerId);

        // Control: unfrozen, this call reaches as far as this substrate goes.
        var reached = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.VerifyOp, AgentId: workerId));
        Assert.Contains("merge queue to verify against", reached.Error ?? "", StringComparison.Ordinal);

        Rig.Sessions.MarkState(KeyFor(workerId), AgentSessionReconciler.PausedState, "conflicted rebase");

        var refused = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.VerifyOp, AgentId: workerId));

        Assert.False(refused.Ok);
        var error = refused.Error ?? string.Empty;
        Assert.Contains("cannot be verified", error, StringComparison.Ordinal);
        Assert.Contains("frozen jail runs nothing", error, StringComparison.Ordinal);
        // It stopped at the pause, not at the substrate's missing queue.
        Assert.DoesNotContain("merge queue to verify against", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>request_verification</c> gets PAST the two checks that guard it — ownership and the plan gate —
    /// for an approved worker the caller owns, and fails only where this substrate genuinely cannot go:
    /// there is no provisioned merge queue behind a fake environment.
    ///
    /// <para>Stated plainly because the distinction is the whole point of the file: this is <b>not</b> a
    /// proof that verification runs. It is a proof that the refusals in
    /// <see cref="CoordinatorRoleLockTests"/> come from the guards rather than from the op being inert —
    /// which is the specific thing 29 refusal-only assertions could not tell anyone. The true positive
    /// needs a real repo and queue and belongs to the Docker tier.</para>
    /// </summary>
    [Fact]
    public async Task RequestVerification_ForAnApprovedOwnedWorker_ReachesTheQueueStep()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("verify me");
        await ApproveAsync(workerId);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.VerifyOp, AgentId: workerId));

        Assert.False(response.Ok); // no queue behind the fake environment
        var error = response.Error ?? string.Empty;

        // It did NOT stop at ownership...
        Assert.DoesNotContain($"no worker '{workerId}'", error, StringComparison.Ordinal);
        // ...nor at the plan gate...
        Assert.DoesNotContain("no work is authorised", error, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting on your approval", error, StringComparison.Ordinal);
        // ...it reached the merge-queue lookup, which is as far as this substrate goes.
        Assert.Contains("merge queue to verify against", error, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task ApproveAsync(string workerId)
    {
        var presenting = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, Title: "T", PlanJson: PlanJson("src/a.cs")));
        var pending = await WaitForAsync(() =>
            Rig.Plans.LiveForWorker(workerId) is { Status: PlanStatus.Pending } p ? p : null);
        Rig.Plans.Approve(pending.PlanId, "os:positives");
        await presenting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Rig.Gate.MayWork(workerId, out _), "setup failed: the worker is not authorised");
    }

    private AgentSessionKey KeyFor(string workerId) =>
        Rig.Sessions.List().Single(s => s.Id == workerId).Key;
}
