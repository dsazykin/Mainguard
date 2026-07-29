using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// P2-10 RequiresDocker legs proven against a <b>real</b> container runtime (plan §6 tests 7/12/13,
/// TI-P2-10.9) — the launch-blocking OPS SA-1 / M7-exit guarantees. Unlike the fast
/// <c>VerificationRunnerTests</c> (which model the sandbox with a fake), these spawn a real
/// <c>busybox</c> container and run the verification through the real <see cref="DockerSandboxEngine"/>
/// so pass/fail comes from an actual <c>docker exec</c> exit reported by the container runtime — the
/// value a compromised, non-TCB supervisor cannot forge. Gated on Docker-daemon presence only (busybox,
/// not the P2-07 agent-base image), so a Docker-less box skips and the CI <c>sandbox-security</c> leg
/// runs them.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class MergeQueueDockerTests
{
    private const string Image = "busybox:latest";

    /// <summary>
    /// OPS SA-1 (plan §6.12 / §9 test 14): the recorded verdict is the daemon-observed container-runtime
    /// exit — a failing command yields <c>Passed:false</c> and no mergeable state <b>even though the
    /// container's own stdout emits a forged "passed" claim</b> (a stand-in for a compromised supervisor's
    /// OOB <c>VerifyResult{passed:true}</c>). The one passing control shows the same real exec drives a
    /// genuine <c>Verified</c>.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        if (!await EnsureImageAsync(docker, ct))
        {
            return; // Docker up but no registry access — nothing to prove.
        }

        await using var jail = await Jail.StartAsync(docker, ct);
        var engine = new DockerSandboxEngine(docker, new SandboxEngineOptions(string.Empty, string.Empty));
        var runner = new VerificationRunner(engine, NewArtifactDir());

        // The verification command forges a "passed" claim on stdout but exits non-zero. The runner must
        // read ONLY the container-runtime exit — so the record is a failure.
        var forged = new[] { "sh", "-c", "echo 'VERIFY_RESULT=passed'; exit 7" };
        var record = await runner.RunAsync(
            new VerificationRequest("attacker", jail.ContainerId, "mainsha", forged, "make check", "cfg"), ct);

        Assert.False(record.Passed);              // the forged stdout claim was ignored; exit 7 wins.

        // End-to-end: a failing real-container verdict can reach no mergeable state, and the forged claim
        // has no entry point into the queue (the queue only ever sees the runner's daemon-observed record).
        var verStore = new InMemoryVerificationStore();
        var queue = new MergeQueue(
            "repo", "mainsha", new InMemoryMergeQueueStore(), verStore,
            runVerification: (id, c) => runner.RunAsync(
                new VerificationRequest(id, jail.ContainerId, "mainsha", forged, "make check", "cfg"), c),
            requeue: (id, c) => Task.CompletedTask);

        await queue.RunVerificationAsync("attacker", ct);
        Assert.NotEqual(WorkerMergeState.Verified, queue.GetState("attacker"));
        Assert.False(queue.CanMerge("attacker", out _));

        // Control: the identical path with a passing command IS Verified — the real exec drives both verdicts.
        var passRunner = new VerificationRunner(engine, NewArtifactDir());
        var passQueue = new MergeQueue(
            "repo", "mainsha", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            runVerification: (id, c) => passRunner.RunAsync(
                new VerificationRequest(id, jail.ContainerId, "mainsha", new[] { "sh", "-c", "exit 0" }, "make check", "cfg"), c),
            requeue: (id, c) => Task.CompletedTask);
        await passQueue.RunVerificationAsync("honest", ct);
        Assert.Equal(WorkerMergeState.Verified, passQueue.GetState("honest"));
    }

    /// <summary>
    /// TI-P2-10.9 / plan §6.13: the command runs inside the worker's container, never on the host. A
    /// uniquely-named marker written by the verification command is present in the container filesystem
    /// (proved by a second real <c>docker exec</c>) and absent from the daemon host's filesystem.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Verification_ShouldRunInWorkerSandbox_NeverHost()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        if (!await EnsureImageAsync(docker, ct))
        {
            return;
        }

        await using var jail = await Jail.StartAsync(docker, ct);
        var engine = new DockerSandboxEngine(docker, new SandboxEngineOptions(string.Empty, string.Empty));
        var runner = new VerificationRunner(engine, NewArtifactDir());

        var markerPath = "/tmp/mainguard-verify-" + Guid.NewGuid().ToString("N");
        var record = await runner.RunAsync(
            new VerificationRequest("a1", jail.ContainerId, "mainsha",
                new[] { "sh", "-c", $"echo ran > {markerPath}" }, "make check", "cfg"), ct);
        Assert.True(record.Passed);

        // Present INSIDE the container (the command's writes landed in the container filesystem)...
        var inContainer = await engine.ExecAsync(jail.ContainerId, new[] { "test", "-f", markerPath }, ct);
        Assert.Equal(0, inContainer.ExitCode);

        // ...and ABSENT on the daemon host (the command never executed on the host — G-11 / rejection trigger).
        Assert.False(File.Exists(markerPath));
    }

    /// <summary>
    /// Plan §6.7 / TI-P2-10.7: the canonical two-worker cascade with <b>real container verification</b>.
    /// A and B each verify green in their own container; A merges (main moves) → B flips
    /// <c>StaleVerified</c> and <c>CanMerge(B)</c> is false; the auto re-queue re-verifies B against the
    /// NEW main in its container → <c>Verified</c> and <c>CanMerge(B)</c> is true again — each verdict
    /// coming from an actual <c>docker exec</c>.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task TwoWorkers_StaleCascade_WithRealContainerVerification()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        if (!await EnsureImageAsync(docker, ct))
        {
            return;
        }

        await using var jailA = await Jail.StartAsync(docker, ct);
        await using var jailB = await Jail.StartAsync(docker, ct);
        var engine = new DockerSandboxEngine(docker, new SandboxEngineOptions(string.Empty, string.Empty));
        var runner = new VerificationRunner(engine, NewArtifactDir());
        var containerFor = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = jailA.ContainerId,
            ["B"] = jailB.ContainerId,
        };

        MergeQueue queue = null!;
        // The real verification: run the (passing) test command in the agent's own container against the
        // queue's CURRENT main sha, so a re-verify after a merge records the new sha.
        Task<VerificationRecord> Run(string id, CancellationToken c) => runner.RunAsync(
            new VerificationRequest(id, containerFor[id], queue.CurrentMainSha,
                new[] { "sh", "-c", "exit 0" }, "make check", "cfg"), c);

        // A gate so the intermediate StaleVerified/CanMerge-false window is observable before re-verify.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Requeue(string id, CancellationToken c)
        {
            await gate.Task.ConfigureAwait(false);
            await queue.RunVerificationAsync(id, c).ConfigureAwait(false);
        }

        var verStore = new InMemoryVerificationStore();
        queue = new MergeQueue("repo", "sha0", new InMemoryMergeQueueStore(), verStore, Run, Requeue);

        await queue.RunVerificationAsync("A", ct);
        await queue.RunVerificationAsync("B", ct);
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("A"));
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("B"));
        Assert.True(queue.CanMerge("B", out _));

        // A merges → main moves. Every other Verified worker goes StaleVerified and cannot merge.
        queue.NotifyMainMoved("sha1");
        Assert.Equal(WorkerMergeState.StaleVerified, queue.GetState("B"));
        Assert.False(queue.CanMerge("B", out _));

        // Release the re-verify: B re-verifies in its container against the new main → Verified + mergeable.
        gate.SetResult();
        await WaitUntilAsync(() => queue.GetState("B") == WorkerMergeState.Verified, ct);
        Assert.True(queue.CanMerge("B", out _));

        // The freshest B record is pinned to the NEW main sha (it re-verified against sha1, not sha0).
        Assert.Equal("sha1", verStore.Latest("repo", "B")!.MainSha);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        Assert.True(condition(), "condition not reached within the timeout");
    }

    private static string NewArtifactDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-verify-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<bool> EnsureImageAsync(IDockerClient docker, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(Image, ct);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            // fall through to pull
        }

        try
        {
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "busybox", Tag = "latest" },
                authConfig: null, progress: new Progress<JSONMessage>(), cancellationToken: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A real, long-lived busybox container standing in for one worker's sandbox; force-removed on dispose.</summary>
    private sealed class Jail : IAsyncDisposable
    {
        private readonly IDockerClient _docker;
        public string ContainerId { get; private set; } = string.Empty;

        private Jail(IDockerClient docker) => _docker = docker;

        public static async Task<Jail> StartAsync(IDockerClient docker, CancellationToken ct)
        {
            var jail = new Jail(docker);
            var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = "mainguard-verify-test-" + Guid.NewGuid().ToString("N")[..8],
                Cmd = new List<string> { "sleep", "300" },
            }, ct);
            jail.ContainerId = created.ID;
            await docker.Containers.StartContainerAsync(jail.ContainerId, new ContainerStartParameters(), ct);
            return jail;
        }

        public async ValueTask DisposeAsync()
        {
            if (string.IsNullOrEmpty(ContainerId))
            {
                return;
            }

            try
            {
                await _docker.Containers.RemoveContainerAsync(ContainerId, new ContainerRemoveParameters { Force = true });
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }
}
