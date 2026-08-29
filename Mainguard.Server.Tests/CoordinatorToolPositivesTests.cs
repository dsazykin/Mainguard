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
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "prefer the stdlib"));

        Assert.True(response.Ok, $"an approved worker with a live CLI refused a prompt: {response.Error}");

        var submitted = await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "prefer the stdlib" }, submitted);

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

        foreach (var text in new[] { "use the stdlib", "and add a test" })
        {
            var response = await CallAsync(coordinatorId, new AgentIpcRequest(
                AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: text));
            Assert.True(response.Ok, $"a steer was refused: {response.Error}");
        }

        var submitted = await cli.WaitForSubmittedAsync(2, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "use the stdlib", "and add a test" }, submitted);
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
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "drop the retry loop\r\nkeep the timeout\n"));

        Assert.True(response.Ok, $"a multi-line steer was refused: {response.Error}");

        var submitted = await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "drop the retry loop\nkeep the timeout" }, submitted);
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// The observation the coordinator is given, in both readings. A CLI that repaints after Enter is
    /// reported as having reacted; a CLI that stays silent is reported as <b>not</b> having reacted —
    /// still <c>Ok</c>, because the daemon really did press Enter, but no longer indistinguishable from
    /// it. The old response said "PromptSent" either way, and the shim printed the worker id instead of
    /// the status, so nothing about a steer's fate reached the caller at all.
    /// </summary>
    [Theory]
    [InlineData(true, "redrew in response")]
    [InlineData(false, "produced no output")]
    public async Task SendWorkerPrompt_ReportsWhetherTheCliWasSeenReacting(bool redraws, string expected)
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync($"reaction {redraws}");
        await ApproveAsync(workerId);

        using var cli = new RawModeCliDouble(redraws);
        using var bound = new BoundTerminalSession(workerId, cli);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "status?"));

        Assert.True(response.Ok, $"a steer was refused: {response.Error}");
        Assert.Contains(expected, response.Status ?? string.Empty, StringComparison.Ordinal);

        // Either way the line WAS submitted — the reaction is an observation about the CLI, not a
        // second opinion about whether Enter was pressed.
        Assert.Equal(new[] { "status?" }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));

        // And the caller can actually see it: the shim prints `status` only when no agentId is present.
        Assert.Null(response.AgentId);
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
