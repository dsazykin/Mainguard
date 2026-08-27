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
/// failing. Binding a real <see cref="BoundTerminalSession"/> over a stub gives the delivery path
/// somewhere to land, and the assertion is on the BYTES that arrive, not on the response flag: a
/// handler that returned <c>Ok</c> without writing anything is the exact silent no-op the trailing
/// newline in <c>TrySendPromptAsync</c> exists to prevent.</para>
/// </summary>
public sealed class CoordinatorToolPositivesTests : PlanGateIpcTestBase, IClassFixture<PlanGateRig>
{
    public CoordinatorToolPositivesTests(PlanGateRig rig) : base(rig)
    {
    }

    /// <summary>
    /// An approved worker with a live CLI receives the prompt, and the CLI receives the BYTES — with the
    /// trailing newline that submits them. Without the newline the text sits in the agent's input buffer
    /// and nothing happens, which would look like a delivered prompt to everything upstream.
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_ReachesAnApprovedWorkersCli_AsASubmittedLine()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("tidy the retry helper");
        await ApproveAsync(workerId);

        var stub = new StubSession();
        using var bound = new BoundTerminalSession(workerId, stub);
        Rig.Terminals.Bind(KeyFor(workerId), bound);

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "prefer the stdlib"));

        Assert.True(response.Ok, $"an approved worker with a live CLI refused a prompt: {response.Error}");
        Assert.Equal("PromptSent", response.Status);

        var written = await stub.ReadWrittenAsync();
        Assert.Equal("prefer the stdlib\n", written);
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

    /// <summary>A terminal whose writes can be read back — the only way to assert delivery rather than
    /// a return value.</summary>
    private sealed class StubSession : ITerminalSession
    {
        private readonly MemoryStream _io = new();

        public Stream IO => _io;

        public Task<int> ExitCode { get; } = new TaskCompletionSource<int>().Task;

        public void Resize(int cols, int rows)
        {
        }

        public void Kill()
        {
        }

        public void Dispose() => _io.Dispose();

        /// <summary>The bytes written toward the CLI, once the write has actually landed.</summary>
        public async Task<string> ReadWrittenAsync()
        {
            for (var i = 0; i < 200 && _io.Length == 0; i++)
            {
                await Task.Delay(10);
            }

            return Encoding.UTF8.GetString(_io.ToArray());
        }
    }
}
