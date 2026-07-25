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
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-10 — the merge queue is actually INSTANTIATED by the running daemon.
///
/// <para><c>new MergeQueue(...)</c>, <c>new MergeQueueContext(...)</c> and <c>registry.Register(...)</c>
/// appeared only in the test projects. The daemon registered an empty <see cref="IMergeQueueRegistry"/> and
/// nothing ever wrote to it, so <c>MergeQueueGrpcService.Resolve</c> threw <c>NOT_FOUND</c> for every handle
/// for the daemon's whole lifetime — and the client's queue pump swallowed it and retried against an empty
/// projection, which is why nobody noticed. The P2-10 merge guarantees were therefore neither enforced nor
/// bypassable: they were not running at all.</para>
///
/// <para>These run through the REAL composition root (<see cref="DaemonFixture"/> = the whole
/// <c>DaemonHost</c> graph) against a REAL provisioned bare mirror, and drive the shipped RPCs. Nothing is
/// hand-registered into the registry: if the daemon does not build the queue itself, they fail with the
/// exact <c>NOT_FOUND</c> the finding describes.</para>
/// </summary>
public sealed class MergeQueueWiringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ProvisionedRepo_YieldsANonNotFoundStreamQueue_ThroughTheRealCompositionRoot()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        // The ONLY thing this test does to make the repo active is call the shipped ProvisionRepo RPC.
        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        Assert.False(string.IsNullOrWhiteSpace(provisioned.RepoHandle));

        using var cts = new CancellationTokenSource(Timeout);
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = provisioned.RepoHandle }, headers, cancellationToken: cts.Token);

        // Pre-fix this MoveNext threw RpcException/NotFound: "No active merge queue for repo handle '…'".
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        var snapshot = stream.ResponseStream.Current;

        // A live queue reports the mirror's real main sha — not an empty projection.
        Assert.False(string.IsNullOrWhiteSpace(snapshot.MainSha));
        Assert.Equal(repos.MainSha, snapshot.MainSha);
    }

    [Fact]
    public async Task CreateWorktree_PutsTheAgentIntoTheLiveQueue()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = "loom-7",
        }, headers);

        using var cts = new CancellationTokenSource(Timeout);
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = provisioned.RepoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));

        // The queue only reports agents it tracks; without the EnsureEntry wiring the repo has branches but
        // an empty queue, which renders as "no work in flight" while work is very much in flight.
        var entry = Assert.Single(stream.ResponseStream.Current.Entries);
        Assert.Equal("loom-7", entry.AgentId);
        Assert.Equal(WorkerMergeState.Working.ToString(), entry.State);
        Assert.False(entry.CanMerge); // unverified — the gate is live, not a default-true placeholder.
    }

    /// <summary>
    /// MG-29's boot merge-reconcile cascade resolves its queue out of this registry, so it only becomes live
    /// once something populates it. This asserts the interaction end to end: the daemon-built queue for a
    /// provisioned repo is reachable through the same <c>Resolve(repoHash)</c> the reconcile callback uses.
    /// </summary>
    [Fact]
    public async Task TheDaemonBuiltQueue_IsReachableByTheBootReconcileCallbackShape()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (_, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);

        var registry = host.Services.GetRequiredService<IMergeQueueRegistry>();
        var resolved = registry.Resolve(provisioned.RepoHandle);

        Assert.NotNull(resolved);
        Assert.Equal(repos.MainSha, resolved!.Queue.CurrentMainSha);
        // The lease store MUST be the daemon's shared singleton, or cross-origin merge serialization
        // silently reverts: BeginMerge and the foreground/external merges would contend on different rows.
        Assert.Same(host.Services.GetRequiredService<IMergeLeaseStore>(), resolved.Leases);
    }

    // ---- helpers ---------------------------------------------------------

    // The real composition root with ONE override: the substrate points at an isolated temp VM root so
    // provisioning never writes to the developer's ~/mainguard. Everything else — the registry, the
    // provisioner, the gRPC services — is the shipped graph.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> NewHost(string vmRoot)
        => new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: vmRoot))));

    private static (MergeQueueService.MergeQueueServiceClient Merge,
        RepoSyncService.RepoSyncServiceClient Sync, Metadata Headers) NewClients(
            Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata
        {
            { "authorization", $"bearer {host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };
        return (new MergeQueueService.MergeQueueServiceClient(channel),
            new RepoSyncService.RepoSyncServiceClient(channel), headers);
    }

    /// <summary>A seeded source repo plus an isolated VM root, so provisioning never touches ~/mainguard.</summary>
    private sealed class TempRepos : IDisposable
    {
        public string VmRoot { get; }
        public string Source { get; }
        public string MainSha { get; }

        public TempRepos()
        {
            VmRoot = NewDir("mainguard-mqwire-vm-");
            Source = NewDir("mainguard-mqwire-src-");

            Repository.Init(Source);
            using var repo = new Repository(Source);
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
            File.WriteAllText(Path.Combine(Source, "README.md"), "seed\n");
            Commands.Stage(repo, "README.md");
            var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
            MainSha = repo.Commit("seed commit", sig, sig).Sha;
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
