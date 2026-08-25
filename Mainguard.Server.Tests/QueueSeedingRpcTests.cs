using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The dev-only seeding surface end to end: a seeding-enabled in-proc daemon (the REAL composition
/// root, one substrate override to an isolated VM root — the `MergeQueueWiringTests` posture), a REAL
/// origin repo, the shipped `ProvisionRepo` RPC, and then the QueueSeedingService RPCs driving the
/// shipped queue. What these prove beyond `QueueSeederTests` is the WIRE: request mapping, the
/// daemon-derived actor, refusal-as-response, and that the seeded entries are exactly what the
/// ordinary merge-queue RPCs then serve.
/// </summary>
public sealed class QueueSeedingRpcTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task SeedsABatch_ThatTheOrdinaryQueueRpcsThenServe()
    {
        using var repos = new TempRepos();
        using var host = NewSeedingHost(repos.VmRoot);
        var (seeding, merge, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        var repoHandle = provisioned.RepoHandle;

        var response = await seeding.SeedQueueEntriesAsync(new SeedQueueEntriesRequest
        {
            RepoHandle = repoHandle,
            Entries =
            {
                new SeedEntrySpec { TargetState = "Working" },
                new SeedEntrySpec { TargetState = "Verified" },
                new SeedEntrySpec { TargetState = "AwaitingReview" },
                new SeedEntrySpec { TargetState = "Rejected", Reason = "seeded no" },
                new SeedEntrySpec { TargetState = "Discarded", Reason = "seeded tidy" },
            },
        }, headers, deadline: DateTime.UtcNow + Timeout);

        Assert.All(response.Results, r => Assert.Equal("", r.Refusal));
        Assert.Equal(new[] { "Working", "Verified", "AwaitingReview", "Rejected", "Discarded" },
            response.Results.Select(r => r.ReachedState).ToArray());
        // The repo had no .mainguard/verify — the seeder provisioned one and said so.
        Assert.True(response.ProvisionedVerifyConfig);

        // The seeded Verified entry is mergeable by the SHIPPED CanMerge RPC — same queue, same gates.
        var verified = response.Results[1].AgentId;
        var can = await merge.CanMergeAsync(
            new CanMergeRequest { RepoHandle = repoHandle, AgentId = verified }, headers);
        Assert.True(can.CanMerge, can.Reason);

        // ...and the status RPC enumerates the batch from the daemon's own registry.
        var status = await seeding.GetSeedingStatusAsync(new GetSeedingStatusRequest(), headers);
        Assert.True(status.Enabled);
        Assert.All(response.Results, r => Assert.Contains($"{repoHandle}/{r.AgentId}", status.SeededEntries));
    }

    [Fact]
    public async Task StalePair_TheMergedSpecReallyStalesTheEarlierSeed_OverTheWire()
    {
        using var repos = new TempRepos();
        using var host = NewSeedingHost(repos.VmRoot);
        var (seeding, merge, sync, headers) = NewClients(host);
        var repoHandle = (await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers)).RepoHandle;

        var response = await seeding.SeedQueueEntriesAsync(new SeedQueueEntriesRequest
        {
            RepoHandle = repoHandle,
            Entries =
            {
                new SeedEntrySpec { TargetState = "Verified", StaleBehavior = "HOLD" },
                new SeedEntrySpec { TargetState = "Merged" },
            },
        }, headers, deadline: DateTime.UtcNow + Timeout);

        Assert.All(response.Results, r => Assert.Equal("", r.Refusal));
        // Final states, not per-step ones: the merge's real cascade staled the first seed.
        Assert.Equal("StaleVerified", response.Results[0].ReachedState);
        Assert.Equal("Merged", response.Results[1].ReachedState);

        // The merge REALLY landed: origin main is the batch's reported main.
        using var origin = new Repository(repos.Source);
        Assert.Equal(response.MainSha, origin.Head.Tip.Sha);

        // The stale entry's gate reason over the shipped RPC is the cascade's own vocabulary.
        var can = await merge.CanMergeAsync(
            new CanMergeRequest { RepoHandle = repoHandle, AgentId = response.Results[0].AgentId }, headers);
        Assert.False(can.CanMerge);
    }

    [Fact]
    public async Task PushCommits_InvalidatesAVerifiedSeed_OverTheWire()
    {
        using var repos = new TempRepos();
        using var host = NewSeedingHost(repos.VmRoot);
        var (seeding, _, sync, headers) = NewClients(host);
        var repoHandle = (await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers)).RepoHandle;

        var seeded = await seeding.SeedQueueEntriesAsync(new SeedQueueEntriesRequest
        {
            RepoHandle = repoHandle,
            Entries = { new SeedEntrySpec { TargetState = "Verified" } },
        }, headers, deadline: DateTime.UtcNow + Timeout);
        var agentId = seeded.Results[0].AgentId;

        var push = await seeding.PushCommitsAsync(
            new PushCommitsRequest { RepoHandle = repoHandle, AgentId = agentId, Count = 1 }, headers);

        Assert.True(push.Pushed, push.Refusal);
        Assert.Equal("Working", push.State);
        Assert.NotEqual("", push.NewTipSha);
    }

    [Fact]
    public async Task ClearSeededEntries_RemovesEverything_AndOnlySeeds()
    {
        using var repos = new TempRepos();
        using var host = NewSeedingHost(repos.VmRoot);
        var (seeding, _, sync, headers) = NewClients(host);
        var repoHandle = (await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers)).RepoHandle;

        await seeding.SeedQueueEntriesAsync(new SeedQueueEntriesRequest
        {
            RepoHandle = repoHandle,
            Entries = { new SeedEntrySpec { TargetState = "Verified", Count = 2 } },
        }, headers, deadline: DateTime.UtcNow + Timeout);

        var cleared = await seeding.ClearSeededEntriesAsync(
            new ClearSeededEntriesRequest { RepoHandle = repoHandle }, headers,
            deadline: DateTime.UtcNow + Timeout);

        Assert.Equal(2, cleared.ClearedAgentIds.Count);
        Assert.Empty(cleared.Failures);

        var status = await seeding.GetSeedingStatusAsync(new GetSeedingStatusRequest(), headers);
        Assert.Empty(status.SeededEntries);
    }

    [Fact]
    public async Task AnUnknownRepoHandle_IsATypedNotFound()
    {
        using var repos = new TempRepos();
        using var host = NewSeedingHost(repos.VmRoot);
        var (seeding, _, _, headers) = NewClients(host);

        var ex = await Assert.ThrowsAsync<RpcException>(() => seeding.SeedQueueEntriesAsync(
            new SeedQueueEntriesRequest
            {
                RepoHandle = "no-such-repo",
                Entries = { new SeedEntrySpec { TargetState = "Working" } },
            }, headers).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task AnUnknownTargetState_IsATypedInvalidArgument_NamingTheVocabulary()
    {
        using var repos = new TempRepos();
        using var host = NewSeedingHost(repos.VmRoot);
        var (seeding, _, _, headers) = NewClients(host);

        var ex = await Assert.ThrowsAsync<RpcException>(() => seeding.SeedQueueEntriesAsync(
            new SeedQueueEntriesRequest
            {
                RepoHandle = "any",
                Entries = { new SeedEntrySpec { TargetState = "Shipped" } },
            }, headers).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("StaleVerified", ex.Status.Detail);
    }

    // ---- helpers ---------------------------------------------------------

    // The real composition root, seeding-enabled, with ONE substrate override to an isolated VM root
    // (the MergeQueueWiringTests posture) so provisioning never writes the developer's ~/mainguard.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> NewSeedingHost(string vmRoot)
        => new DaemonFixture { EnableQueueSeeding = true }.WithWebHostBuilder(b => b.ConfigureTestServices(
            services => services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: vmRoot))));

    private static (QueueSeedingService.QueueSeedingServiceClient Seeding,
        MergeQueueService.MergeQueueServiceClient Merge,
        RepoSyncService.RepoSyncServiceClient Sync, Metadata Headers) NewClients(
            Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata
        {
            { "authorization", $"bearer {host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };
        return (new QueueSeedingService.QueueSeedingServiceClient(channel),
            new MergeQueueService.MergeQueueServiceClient(channel),
            new RepoSyncService.RepoSyncServiceClient(channel), headers);
    }

    /// <summary>A seeded source repo plus an isolated VM root (no .mainguard/verify on purpose — the
    /// auto-provision arm is part of what the wire tests cover).</summary>
    private sealed class TempRepos : IDisposable
    {
        public string VmRoot { get; }
        public string Source { get; }

        public TempRepos()
        {
            VmRoot = NewDir("mainguard-seedrpc-vm-");
            Source = NewDir("mainguard-seedrpc-src-");

            Repository.Init(Source);
            using var repo = new Repository(Source);
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
            File.WriteAllText(Path.Combine(Source, "README.md"), "seed\n");
            Commands.Stage(repo, "README.md");
            var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
            repo.Commit("seed commit", sig, sig);
        }

        public void Dispose()
        {
            TryDelete(VmRoot);
            TryDelete(Source);
        }

        private static string NewDir(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
            catch { /* never fail a test from cleanup */ }
        }
    }
}
