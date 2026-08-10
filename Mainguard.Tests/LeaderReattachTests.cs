using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-09 test 7 (pure part): the leader's durable-registry round-trip and the boot reattach
/// reconcile — a registry session whose container is not live is reaped, one whose container is live is
/// reattached (Docker-as-truth). The full cross-process <c>kill -9</c> survival is the RequiresDocker
/// case in <c>Mainguard.Server.Tests</c>; this pins the reconcile logic without Docker.
/// </summary>
public sealed class LeaderReattachTests : IDisposable
{
    private readonly string _dir;

    public LeaderReattachTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mainguard-leader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Registry_RoundTrips_UpsertRemove()
    {
        var registry = new LeaderRegistry(Path.Combine(_dir, "sessions.json"));

        registry.Upsert(new LeaderSession("a1", "repo1", "cid-1", 80, 24, "/run/leader/a1.sock"));
        registry.Upsert(new LeaderSession("a2", "repo1", "cid-2", 120, 40, "/run/leader/a2.sock"));

        var loaded = registry.Load().OrderBy(s => s.AgentId, StringComparer.Ordinal).ToList();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("cid-1", loaded[0].ContainerId);
        Assert.Equal(120, loaded[1].Cols);

        // Upsert replaces (keyed by agent id), never duplicates.
        registry.Upsert(new LeaderSession("a1", "repo1", "cid-1b", 80, 24, "/run/leader/a1.sock"));
        Assert.Equal("cid-1b", registry.Load().Single(s => s.AgentId == "a1").ContainerId);

        registry.Remove("a1");
        Assert.DoesNotContain(registry.Load(), s => s.AgentId == "a1");

        // A fresh registry over the same file sees the persisted state.
        Assert.Single(new LeaderRegistry(Path.Combine(_dir, "sessions.json")).Load());
    }

    /// <summary>
    /// The boot step must leave an ARTIFACT of the sessions it reaped. Its
    /// <see cref="LeaderReconcileReport"/> used to be discarded — and reaping a leader session KILLS the
    /// agent's PTY and drops it from the durable registry, so a boot pass could silently end every
    /// terminal a user left running with nothing anywhere recording which ones.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task BootStep_RecordsWhatItReaped_RatherThanDiscardingTheReport()
    {
        var registry = new LeaderRegistry(Path.Combine(_dir, "sessions.json"));
        var leader = new SessionLeader(registry);
        leader.Register(new LeaderSession("alive", "repo1", "cid-1", 80, 24, "/s/alive"));
        leader.Register(new LeaderSession("overnight", "repo1", "cid-2", 80, 24, "/s/overnight"));

        var audit = new Mainguard.Git.Audit.InMemoryAuditLog();
        var lines = new System.Collections.Generic.List<string>();
        var task = new LeaderReattachTask(
            leader,
            _ => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<AgentContainerState>>(
                new[] { new AgentContainerState("alive", "repo1", "cid-1", Running: true) }),
            audit,
            lines.Add);

        await task.RunAsync(System.Threading.CancellationToken.None);

        Assert.NotNull(task.LastReport);
        Assert.Equal(new[] { "overnight" }, task.LastReport!.Reaped);

        var entry = Assert.Single(audit.Read(), e => e.Type == LeaderReattachTask.ReattachedEvent);
        Assert.Equal("overnight", entry.Fields["reaped"]);
        Assert.Equal("alive", entry.Fields["reattached"]);
        Assert.Contains(lines, l => l.Contains("overnight", StringComparison.Ordinal));
    }

    [Fact]
    public void Reattach_ReapsDeadContainers_KeepsLive()
    {
        var registry = new LeaderRegistry(Path.Combine(_dir, "sessions.json"));
        var leader = new SessionLeader(registry);

        var a1Killed = false;
        var a2Killed = false;
        leader.Register(new LeaderSession("a1", "repo1", "cid-1", 80, 24, "/s/a1"), () => a1Killed = true);
        leader.Register(new LeaderSession("a2", "repo1", "cid-2", 80, 24, "/s/a2"), () => a2Killed = true);

        // Docker truth: only a1's container is live.
        var report = leader.Reattach(new[]
        {
            new AgentContainerState("a1", "repo1", "cid-1", Running: true),
        });

        Assert.Equal(new[] { "a1" }, report.Reattached);
        Assert.Equal(new[] { "a2" }, report.Reaped);
        Assert.False(a1Killed);
        Assert.True(a2Killed); // the dead session's PTY was reaped

        // Registry now reflects only the survivor.
        Assert.Equal(new[] { "a1" }, registry.Load().Select(s => s.AgentId).ToArray());
        Assert.True(leader.HasSession("a1"));
        Assert.False(leader.HasSession("a2"));
    }

    [Fact]
    public void PauseResumeInput_TogglesLeaderState()
    {
        var leader = new SessionLeader(new LeaderRegistry(Path.Combine(_dir, "sessions.json")));
        leader.Register(new LeaderSession("a1", "repo1", "cid-1", 80, 24, "/s/a1"));

        Assert.False(leader.IsPaused("a1"));
        leader.PauseInput("a1");
        Assert.True(leader.IsPaused("a1"));
        leader.ResumeInput("a1");
        Assert.False(leader.IsPaused("a1"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Never fail a test from cleanup.
        }
    }
}
