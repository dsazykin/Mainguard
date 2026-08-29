using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Services;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// H2 and H4 at the daemon's edge — <b>what a client can actually LEARN about a verification</b>.
///
/// <para>A real run produced <c>VerificationRows</c> id 47: <c>Passed=0</c>, exit 1, artifact
/// <c>subtract(5,3) !== 2</c>. The merge-queue row said "not verified yet" and the worker pane said "no
/// verification record yet", both with Verify still on offer. Two separate causes met here, at the wire:
/// the STATE could not express a failure (it settled to <c>Working</c>, which is where a never-run entry
/// sits), and the wire's <c>QueueEntry</c> carried no verification facts at all — so even a client that
/// wanted to say something true had nothing to say it from, and the artifact holding the real output was
/// reachable only by opening the daemon's database by hand.</para>
///
/// <para>These drive the shipped RPCs against the real composition root: if the daemon does not carry the
/// verdict, or cannot serve the output, they fail here rather than in a hand-built projection.</para>
/// </summary>
public sealed class VerificationOutcomeSurfaceTests
{
    private const string RepoHandle = "repo-verdict";
    private const string MainSha = "main-000099";
    private const string AgentId = "loom-red";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // ---- H2: the state on the wire ---------------------------------------

    /// <summary>
    /// The row the human sees says the tests FAILED, and the gate reason is the verdict rather than the
    /// "not verified yet" a never-run entry gets.
    /// </summary>
    [Fact]
    public async Task AFailedVerification_ReachesTheClientAsAFailure_NotAsNotVerifiedYet()
    {
        using var host = NewHost();
        var (merge, headers) = NewClients(host);
        var artifact = WriteArtifact("subtract(5,3) !== 2");
        RegisterQueue(host, passed: false, artifact);

        var entry = await FirstEntryAsync(merge, headers);

        Assert.Equal(nameof(WorkerMergeState.VerificationFailed), entry.State);
        Assert.False(entry.CanMerge);
        Assert.Contains("FAILED", entry.GateReason);
        Assert.DoesNotContain("not verified yet", entry.GateReason);

        // …and the verdict travels as its own fact, so a surface can render it without parsing prose.
        Assert.True(entry.HasLastVerificationPassed);
        Assert.False(entry.LastVerificationPassed);
        Assert.Equal("node test.js", entry.LastVerificationCommand);
        Assert.NotEqual("", entry.LastVerificationAt);
    }

    /// <summary>
    /// The control that keeps H2's whole point intact ON THE WIRE: an entry that has never been verified
    /// carries NO verdict — not a false one.
    ///
    /// <para>proto3 scalars default to false, so a plain <c>bool</c> here would have made "never run" and
    /// "failed" the same value again, one layer below the state fix. That is why the field is
    /// <c>optional</c>, and this is the test that fails if it stops being.</para>
    /// </summary>
    [Fact]
    public async Task AnEntryThatWasNeverVerified_CarriesNoVerdictAtAll()
    {
        using var host = NewHost();
        var (merge, headers) = NewClients(host);
        RegisterQueue(host, passed: null, artifact: null);

        var entry = await FirstEntryAsync(merge, headers);

        Assert.Equal(nameof(WorkerMergeState.Working), entry.State);
        Assert.Equal("not verified yet", entry.GateReason);
        Assert.False(entry.HasLastVerificationPassed);   // ABSENT, not false
        Assert.Equal("", entry.LastVerificationCommand);
        Assert.Equal("", entry.LastVerificationAt);
    }

    // ---- H4: the output ---------------------------------------------------

    /// <summary>
    /// The failure is READABLE. The artifact's real stdout/stderr comes back over the wire, which before
    /// this RPC it could not: the daemon wrote it to a file under its own data directory, recorded the path
    /// in SQLite, and carried none of it anywhere a human could reach.
    /// </summary>
    [Fact]
    public async Task GetVerificationLog_ReturnsTheRunsActualOutput()
    {
        using var host = NewHost();
        var (merge, headers) = NewClients(host);
        var artifact = WriteArtifact("subtract(5,3) !== 2");
        RegisterQueue(host, passed: false, artifact);

        var log = await merge.GetVerificationLogAsync(
            new GetVerificationLogRequest { RepoHandle = RepoHandle, AgentId = AgentId }, headers);

        Assert.True(log.HasRecord);
        Assert.False(log.Passed);
        Assert.Equal("node test.js", log.ResolvedCommand);
        Assert.Contains("subtract(5,3) !== 2", log.Log);
        Assert.False(log.Truncated);
        Assert.Equal("", log.UnavailableReason);

        // The daemon's own filesystem path is NOT on the wire (G-14) — the content is.
        Assert.DoesNotContain(artifact, log.Log);
    }

    /// <summary>
    /// Three answers, kept apart. An entry with no record is not an entry whose tests printed nothing, and
    /// answering the first as the second is the same quiet fabrication as the "not verified yet" this whole
    /// change removes.
    /// </summary>
    [Fact]
    public async Task GetVerificationLog_ForAnEntryNeverVerified_SaysThereIsNoRecord()
    {
        using var host = NewHost();
        var (merge, headers) = NewClients(host);
        RegisterQueue(host, passed: null, artifact: null);

        var log = await merge.GetVerificationLogAsync(
            new GetVerificationLogRequest { RepoHandle = RepoHandle, AgentId = AgentId }, headers);

        Assert.False(log.HasRecord);
        Assert.Equal("", log.Log);
    }

    /// <summary>
    /// …and a record whose artifact is GONE keeps its verdict and says why the output is missing. The
    /// runner's artifact write is best-effort (losing the artifact must not lose the record), so this is a
    /// real state; rendering it as an empty log would tell a human the suite printed nothing.
    /// </summary>
    [Fact]
    public async Task GetVerificationLog_WhenTheArtifactIsGone_KeepsTheVerdict_AndSaysWhy()
    {
        using var host = NewHost();
        var (merge, headers) = NewClients(host);
        var artifact = WriteArtifact("gone in a moment");
        RegisterQueue(host, passed: false, artifact);
        File.Delete(artifact);

        var log = await merge.GetVerificationLogAsync(
            new GetVerificationLogRequest { RepoHandle = RepoHandle, AgentId = AgentId }, headers);

        Assert.True(log.HasRecord);
        Assert.False(log.Passed);
        Assert.Equal("", log.Log);
        Assert.Contains("no longer on disk", log.UnavailableReason);
    }

    /// <summary>
    /// A pathological suite's output is bounded, and the TAIL is what survives — a test runner prints its
    /// failures last, so truncating from the front would truncate away the reason the human opened the log.
    /// The truncation is declared rather than silent.
    /// </summary>
    [Fact]
    public void ReadTail_KeepsTheEndAndSaysItTruncated()
    {
        var path = Path.Combine(Path.GetTempPath(), "mainguard-tail-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, new string('a', 4096) + "THE ASSERTION THAT FAILED");

            var tail = MergeQueueGrpcService.ReadTail(path, 64, out var truncated);

            Assert.True(truncated);
            Assert.Contains("THE ASSERTION THAT FAILED", tail);
            Assert.True(tail.Length <= 64);

            // The control: a small file is returned whole and is not reported as truncated.
            var whole = MergeQueueGrpcService.ReadTail(path, 1 << 20, out var wholeTruncated);
            Assert.False(wholeTruncated);
            Assert.StartsWith("aaaa", whole);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static async Task<Mainguard.Protos.V1.QueueEntry> FirstEntryAsync(
        MergeQueueService.MergeQueueServiceClient merge, Metadata headers)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var stream = merge.StreamQueue(
            new StreamQueueRequest { RepoHandle = RepoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        return Assert.Single(stream.ResponseStream.Current.Entries);
    }

    /// <summary>
    /// A live queue holding ONE entry, optionally already verified with the given verdict. The run is a
    /// stub because the subject here is what the daemon SAYS about a record, not how a record is produced —
    /// that is <c>MergeQueueProvisionerTests</c>' subject, over a real jail exit.
    /// </summary>
    private static void RegisterQueue(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, bool? passed, string? artifact)
    {
        var queue = new MergeQueue(
            repoHash: "verdict-h1",
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => Task.FromResult(
                new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                    agentId, MainSha, passed ?? false, artifact ?? "", "node test.js", "cfg",
                    DateTimeOffset.UtcNow)));

        queue.EnsureEntry(AgentId, MergeEntryOrigin.Local);
        if (passed.HasValue)
        {
            queue.RunVerificationAsync(AgentId, CancellationToken.None).GetAwaiter().GetResult();
        }

        var registry = (MergeQueueRegistry)host.Services.GetRequiredService<IMergeQueueRegistry>();
        registry.Register(RepoHandle, new MergeQueueContext(
            queue, host.Services.GetRequiredService<IMergeLeaseStore>()));
    }

    private static string WriteArtifact(string stderr)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "mainguard-verify-artifact-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path,
            $"agent: {AgentId}\nresolved-command: node test.js\ncontainer-runtime-exit: 1\n"
            + $"---- stdout ----\n\n---- stderr ----\n{stderr}\n");
        return path;
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> NewHost()
        => new DaemonFixture();

    private static (MergeQueueService.MergeQueueServiceClient Merge, Metadata Headers) NewClients(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata
        {
            { "authorization", $"bearer {host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };
        return (new MergeQueueService.MergeQueueServiceClient(channel), headers);
    }
}
