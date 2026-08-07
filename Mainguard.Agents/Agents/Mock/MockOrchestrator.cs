using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Mock;

/// <summary>
/// The scripted stand-in for the Phase-2 daemon (Lane E Part 3). Implements every
/// orchestration seam with a deterministic-enough timeline so the control-center and
/// Vibe prototypes are alive: agents work and verify, the merge queue runs its stale
/// cascade when a merge lands, the coordinator drafts a plan for approval, the kill
/// switch freezes-then-pauses, telemetry samples tick. All state is in-memory; the
/// tick runs on a timer thread — consumers marshal events to the UI thread.
/// No daemon, no Docker, no real agents (Master Doc §9 discipline).
/// </summary>
public sealed class MockOrchestrator :
    IAgentService, IMergeQueueService, ICoordinatorService,
    IKillSwitchService, ITelemetryService, IVibeService, IDisposable
{
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly Random _rng = new(7);
    private long _seq;
    private int _tick;

    private sealed class AgentState
    {
        public required string Id;
        public required string Name;
        public required string Branch;
        public AgentLifecycleState Life;
        public WorkerMergeState Merge;
        public string Detail = "";
        public DateTimeOffset SpawnedAt;
        public int TestsPassed, TestsTotal = 58;
        public VerificationRecord? Verification;
        public List<FlaggedItem> Flagged = new();
        public List<string> Terminal = new();
        public List<(string Step, bool Done)> Plan = new();
        public List<string> QueuedPrompts = new();
        public AgentLifecycleState LifeBeforePause;
        public bool InQueue = true;
        public int Cooldown; // generic per-agent script counter
        public double Cpu, Ram;        // live per-agent usage (task-manager rows)
        public decimal Spend;          // per-agent spend today; the total is their sum
    }

    private readonly List<AgentState> _agents = new();
    private readonly List<ChatLine> _transcript = new();
    private readonly List<TaskPlan> _pendingPlans = new();
    private readonly List<SandboxEvent> _sandbox = new();
    private readonly List<ResourceSample> _samples = new();
    private readonly List<Checkpoint> _checkpoints = new();
    private string _mainSha = "d4e1f9a";
    private bool _frozen;
    private KillSwitchPhase _phase = KillSwitchPhase.Armed;
    private int _phaseTicks;
    private DeployStatus _deploy = new(DeployPhase.Idle, null, null);
    private int _deployTicks = -1;

    public event Action<AgentEvent>? EventReceived;
    public event Action? Changed;          // ICoordinatorService + IKillSwitchService share the app's marshal-and-requery pattern
    public event Action? Sampled;
    public event Action? DeployChanged;

    public MockOrchestrator(TimeSpan? tickInterval = null)
    {
        SeedAgents();
        SeedTranscript();
        SeedTelemetryAndCheckpoints();
        _timer = new Timer(_ => Tick(), null, tickInterval ?? TimeSpan.FromSeconds(1), tickInterval ?? TimeSpan.FromSeconds(1));
    }

    // ---- seeding ----------------------------------------------------------

    private void SeedAgents()
    {
        var now = DateTimeOffset.Now;
        _agents.Add(new AgentState
        {
            Id = "loom-1",
            Name = "Loom-1",
            Branch = "feat/search-index",
            Life = AgentLifecycleState.Working,
            Merge = WorkerMergeState.Verifying,
            Detail = "tests 12/58",
            SpawnedAt = now.AddMinutes(-42),
            TestsPassed = 12,
            Cpu = 24,
            Ram = 0.7,
            Spend = 0.62m,
            Terminal = { "$ dotnet test --filter SearchIndex", "  Discovering tests…", "  Passed: 12" },
            Plan = { ("Read the search service", true), ("Write failing index tests", true), ("Implement incremental index", false) },
        });
        _agents.Add(new AgentState
        {
            Id = "loom-2",
            Name = "Loom-2",
            Branch = "fix/stash-race",
            Life = AgentLifecycleState.Working,
            Merge = WorkerMergeState.StaleVerified,
            Detail = "re-verifying against " + _mainSha,
            SpawnedAt = now.AddMinutes(-71),
            Cooldown = 8,
            Cpu = 9,
            Ram = 0.5,
            Spend = 0.48m,
            Terminal = { "$ git rebase mainguard-vm/main", "  Rebased 3 commits cleanly." },
            Plan = { ("Reproduce the race", true), ("Fix + regression test", true) },
        });
        _agents.Add(new AgentState
        {
            Id = "loom-3",
            Name = "Loom-3",
            Branch = "fix/auth-refresh",
            Life = AgentLifecycleState.AwaitingReview,
            Merge = WorkerMergeState.Verified,
            Detail = "sitting 22 min",
            SpawnedAt = now.AddMinutes(-96),
            TestsPassed = 58,
            Cpu = 2,
            Ram = 0.4,
            Spend = 0.71m,
            Verification = new VerificationRecord("loom-3", "d4e1f9a", true, 58, 58, now.AddMinutes(-22)),
            Flagged =
            {
                new FlaggedItem("f1", "package.json", "ExecutableConfig", "scripts block edited", false),
                new FlaggedItem("f2", "docs/notes.md", "OutOfScope", "outside the approved plan scope (4 files)", false),
            },
            Terminal = { "$ dotnet test", "  Passed: 58, Failed: 0", "Verification green — awaiting review." },
            Plan = { ("Extract ITokenClock", true), ("Inject clock in refresh path", true), ("New expiry cases", true) },
        });
        _agents.Add(new AgentState
        {
            Id = "loom-4",
            Name = "Loom-4",
            Branch = "feat/palette-actions",
            Life = AgentLifecycleState.Working,
            Merge = WorkerMergeState.Working,
            Detail = "editing CommandPaletteView",
            SpawnedAt = now.AddMinutes(-12),
            Cpu = 14,
            Ram = 0.5,
            Spend = 0.33m,
            Terminal = { "$ claude -p \"add palette actions\"", "  Editing Views/CommandPaletteView.axaml…" },
            Plan = { ("Survey ActionRegistry", true), ("Add missing actions", false) },
        });
    }

    private void SeedTranscript()
    {
        var t = DateTimeOffset.Now.AddMinutes(-9);
        _transcript.Add(new ChatLine(ChatLineKind.Human, "Split the auth work from the search work and run them in parallel.", t));
        _transcript.Add(new ChatLine(ChatLineKind.Coordinator, "Two independent tasks: the token-refresh fix touches src/Auth only; the search index touches Core/Search. Drafting a plan for the refresh work — the search worker is already running.", t.AddSeconds(20)));
        _transcript.Add(new ChatLine(ChatLineKind.ToolCall, "get_worker_status(loom-1)", t.AddSeconds(22)));
        _transcript.Add(new ChatLine(ChatLineKind.ToolCall, "spawn_worker(fix/token-expiry)", t.AddSeconds(31)));
        _transcript.Add(new ChatLine(ChatLineKind.SystemLine, "Loom-1 verifying against d4e1f9a — 12 of 58 tests run", t.AddMinutes(2)));
        _pendingPlans.Add(new TaskPlan(
            "plan-7", "Fix token expiry off-by-one",
            new[] { "src/Auth/TokenClock.cs", "src/Auth/RefreshService.cs", "tests/AuthTests.cs" },
            "Extract the clock behind ITokenClock; inject a fixed clock in tests; correct the expiry comparison.",
            "AuthTests green plus two new expiry-boundary cases.",
            1.50m, DateTimeOffset.Now.AddMinutes(-2)));
        _transcript.Add(new ChatLine(ChatLineKind.PlanCard, "TaskPlan #7 — Fix token expiry off-by-one", DateTimeOffset.Now.AddMinutes(-2), "plan-7"));
    }

    private void SeedTelemetryAndCheckpoints()
    {
        var now = DateTimeOffset.Now;
        _sandbox.Add(new SandboxEvent(now.AddMinutes(-31), "loom-2", "egress_denied", "pastebin.com", "curl"));
        _sandbox.Add(new SandboxEvent(now.AddMinutes(-18), "loom-1", "quarantine_push", "agent/loom-1", "git"));
        _samples.Add(new ResourceSample(now, 34, 2.1, 2.14m));
        _checkpoints.Add(new Checkpoint("a1b2c3d", "Added the header component", now.AddMinutes(-26), true));
        _checkpoints.Add(new Checkpoint("b2c3d4e", "Made the header sticky (first try)", now.AddMinutes(-12), false));
    }

    // ---- the tick ---------------------------------------------------------

    private void Tick()
    {
        var raised = new List<AgentEvent>();
        bool coordinatorChanged = false, killChanged = false, deployChanged = false;

        lock (_gate)
        {
            _tick++;
            var now = DateTimeOffset.Now;

            if (_phase is KillSwitchPhase.QueueFrozen or KillSwitchPhase.PerAgentYield or KillSwitchPhase.Frozen or KillSwitchPhase.Snapshotted)
            {
                killChanged = AdvanceKillSwitch(raised, now);
            }
            else if (!_frozen)
            {
                foreach (var a in _agents.Where(x => x.Life != AgentLifecycleState.TornDown))
                    AdvanceAgent(a, raised, now, ref coordinatorChanged);
            }

            // per-agent resource walk; the totals are their sum plus a small host baseline,
            // so the task-manager rows always decompose the header (revised 2026-07-11).
            foreach (var a in _agents.Where(x => x.Life != AgentLifecycleState.TornDown))
            {
                var (cpuLo, cpuHi, rate) = a.Life switch
                {
                    AgentLifecycleState.Paused or AgentLifecycleState.ReviewHibernated => (0.0, 2.0, 0m),
                    AgentLifecycleState.RateLimited => (1.0, 4.0, 0m),
                    AgentLifecycleState.AwaitingReview => (1.0, 5.0, 0m),
                    AgentLifecycleState.Provisioning => (5.0, 20.0, 0m),
                    _ when a.Merge == WorkerMergeState.Verifying => (20.0, 60.0, 0.0016m),
                    _ => (8.0, 40.0, 0.0009m),
                };
                a.Cpu = Math.Clamp(a.Cpu + _rng.Next(-5, 6), cpuLo, cpuHi);
                a.Ram = Math.Clamp(a.Ram + (_rng.NextDouble() - 0.5) * 0.08, 0.3, 1.8);
                if (!_frozen) a.Spend += rate;
            }
            var live = _agents.Where(x => x.Life != AgentLifecycleState.TornDown).ToList();
            var cpu = Math.Clamp(live.Sum(x => x.Cpu) + 4 + _rng.Next(0, 4), 2, 98);
            var ram = Math.Round(0.6 + live.Sum(x => x.Ram), 2);
            var spend = Math.Round(live.Sum(x => x.Spend), 3);
            _samples.Add(new ResourceSample(now, cpu, ram, spend));
            if (_samples.Count > 120) _samples.RemoveAt(0);

            if (_tick == 45)
            {
                _sandbox.Add(new SandboxEvent(now, "loom-4", "egress_denied", "api.telemetry-collector.io", "node"));
                raised.Add(NewEvent("egress_denied", "loom-4", "api.telemetry-collector.io"));
            }

            if (_deployTicks >= 0) deployChanged = AdvanceDeploy(now);
        }

        foreach (var e in raised) EventReceived?.Invoke(e);
        if (coordinatorChanged) Changed?.Invoke();
        if (killChanged) Changed?.Invoke();
        if (deployChanged) DeployChanged?.Invoke();
        Sampled?.Invoke();
    }

    private void AdvanceAgent(AgentState a, List<AgentEvent> raised, DateTimeOffset now, ref bool coordinatorChanged)
    {
        switch (a.Id)
        {
            case "loom-1" when a.Merge == WorkerMergeState.Verifying:
                a.TestsPassed = Math.Min(a.TestsTotal, a.TestsPassed + _rng.Next(1, 4));
                a.Detail = $"tests {a.TestsPassed}/{a.TestsTotal}";
                if (_tick % 3 == 0) a.Terminal.Add($"  Passed: {a.TestsPassed}");
                if (a.TestsPassed >= a.TestsTotal)
                {
                    SetMerge(a, WorkerMergeState.Verified, raised);
                    a.Life = AgentLifecycleState.AwaitingReview;
                    a.Verification = new VerificationRecord(a.Id, _mainSha, true, a.TestsTotal, a.TestsTotal, now);
                    a.Detail = "verified against " + _mainSha;
                    a.Terminal.Add("Verification green — awaiting review.");
                    _transcript.Add(new ChatLine(ChatLineKind.SystemLine, $"{a.Name} verified against {_mainSha} — {a.TestsTotal} tests green", now));
                    coordinatorChanged = true;
                }
                break;

            case "loom-2" when a.Merge == WorkerMergeState.StaleVerified:
                if (--a.Cooldown <= 0)
                {
                    SetMerge(a, WorkerMergeState.Verifying, raised);
                    a.TestsPassed = 0; a.TestsTotal = 41;
                    a.Detail = "tests 0/41";
                    a.Terminal.Add("$ dotnet test  # re-verify after rebase");
                }
                break;

            case "loom-2" when a.Merge == WorkerMergeState.Verifying:
                a.TestsPassed = Math.Min(a.TestsTotal, a.TestsPassed + _rng.Next(2, 5));
                a.Detail = $"tests {a.TestsPassed}/{a.TestsTotal}";
                if (a.TestsPassed >= a.TestsTotal)
                {
                    SetMerge(a, WorkerMergeState.Verified, raised);
                    a.Life = AgentLifecycleState.AwaitingReview;
                    a.Verification = new VerificationRecord(a.Id, _mainSha, true, a.TestsTotal, a.TestsTotal, now);
                    a.Detail = "verified against " + _mainSha;
                }
                break;

            case "loom-4":
                if (a.Life == AgentLifecycleState.Working && _tick % 37 == 0)
                {
                    a.Life = AgentLifecycleState.RateLimited; a.Cooldown = 5; a.Detail = "retrying in ~40s";
                    raised.Add(NewEvent("rate_limited", a.Id, "retryAfter=40"));
                }
                else if (a.Life == AgentLifecycleState.RateLimited && --a.Cooldown <= 0)
                {
                    a.Life = AgentLifecycleState.Working; a.Detail = "editing CommandPaletteView";
                    raised.Add(NewEvent("agent_state", a.Id, "RateLimited→Working"));
                }
                else if (a.Life == AgentLifecycleState.Working && _tick % 4 == 0)
                {
                    a.Terminal.Add("  edit " + (_tick % 8 == 0 ? "CommandPaletteViewModel.cs" : "CommandPaletteView.axaml"));
                }
                break;

            case "loom-5" when a.Life == AgentLifecycleState.Provisioning:
                if (--a.Cooldown <= 0)
                {
                    a.Life = AgentLifecycleState.Working; a.Merge = WorkerMergeState.Working;
                    a.Detail = "reading src/Auth"; a.Terminal.Add("$ claude -p \"fix token expiry\"");
                    raised.Add(NewEvent("agent_state", a.Id, "Provisioning→Working"));
                }
                break;
        }

        // merged and ended entries leave a few ticks after landing
        if ((a.Merge == WorkerMergeState.Merged || a.Life == AgentLifecycleState.Rejected) && a.InQueue && --a.Cooldown <= 0)
        {
            a.InQueue = false;
            a.Life = AgentLifecycleState.TornDown;
            raised.Add(NewEvent("agent_state", a.Id, "→TornDown"));
        }
    }

    private bool AdvanceKillSwitch(List<AgentEvent> raised, DateTimeOffset now)
    {
        _phaseTicks++;
        switch (_phase)
        {
            case KillSwitchPhase.QueueFrozen:
                _phase = KillSwitchPhase.PerAgentYield;
                raised.Add(NewEvent("killswitch", "*", "phase=PerAgentYield"));
                return true;
            case KillSwitchPhase.PerAgentYield when _phaseTicks >= 3:
                foreach (var a in _agents.Where(x => x.Life is AgentLifecycleState.Working or AgentLifecycleState.RateLimited or AgentLifecycleState.Unresponsive))
                { a.LifeBeforePause = a.Life; a.Life = AgentLifecycleState.Paused; a.Detail = "paused"; }
                _phase = KillSwitchPhase.Frozen;
                raised.Add(NewEvent("killswitch", "*", "phase=Frozen"));
                return true;
            case KillSwitchPhase.Frozen:
                _phase = KillSwitchPhase.Snapshotted;
                return true;
            case KillSwitchPhase.Snapshotted:
                _phase = KillSwitchPhase.Complete;
                raised.Add(NewEvent("killswitch", "*", "phase=Complete"));
                return true;
        }
        return false;
    }

    private bool AdvanceDeploy(DateTimeOffset now)
    {
        _deployTicks++;
        var next = _deployTicks switch
        {
            2 => new DeployStatus(DeployPhase.Uploading, null, null),
            4 => new DeployStatus(DeployPhase.Building, null, null),
            7 => new DeployStatus(DeployPhase.GoingLive, null, null),
            9 => new DeployStatus(DeployPhase.Live, "https://myapp.vercel.app", null),
            _ => _deploy,
        };
        if (ReferenceEquals(next, _deploy)) return false;
        _deploy = next;
        if (_deploy.Phase == DeployPhase.Live) _deployTicks = -1;
        return true;
    }

    private void SetMerge(AgentState a, WorkerMergeState to, List<AgentEvent> raised)
    {
        var from = a.Merge;
        a.Merge = to;
        raised.Add(NewEvent("queue_state", a.Id, $"{from}→{to} main@{_mainSha}"));
    }

    private AgentEvent NewEvent(string type, string agentId, string payload)
        => new(Interlocked.Increment(ref _seq), type, agentId, payload, DateTimeOffset.Now);

    // ---- IAgentService ----------------------------------------------------

    public IReadOnlyList<AgentInfo> ListAgents()
    {
        lock (_gate)
            return _agents.Where(a => a.Life != AgentLifecycleState.TornDown)
                .Select(a => new AgentInfo(a.Id, a.Name, a.Branch, a.Life, a.Detail, a.SpawnedAt)).ToList();
    }

    public Task SendPromptAsync(string agentId, string prompt)
    {
        List<AgentEvent> raised = new();
        lock (_gate)
        {
            var a = Find(agentId);
            a.QueuedPrompts.Add(prompt);
            raised.Add(NewEvent("prompt_queued", agentId, prompt));
        }
        foreach (var e in raised) EventReceived?.Invoke(e);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetQueuedPrompts(string agentId) { lock (_gate) return Find(agentId).QueuedPrompts.ToList(); }

    public Task CancelQueuedPromptAsync(string agentId, int index)
    {
        lock (_gate)
        {
            var q = Find(agentId).QueuedPrompts;
            if (index >= 0 && index < q.Count) q.RemoveAt(index);
        }
        EventReceived?.Invoke(NewEvent("prompt_cancelled", agentId, index.ToString()));
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetTerminalTail(string agentId) { lock (_gate) return Find(agentId).Terminal.TakeLast(40).ToList(); }

    public Task PauseAgentAsync(string agentId)
    {
        AgentEvent? evt = null;
        lock (_gate)
        {
            var a = Find(agentId);
            if (a.Life is AgentLifecycleState.Working or AgentLifecycleState.RateLimited
                       or AgentLifecycleState.Unresponsive or AgentLifecycleState.Yielding)
            {
                a.LifeBeforePause = a.Life;
                a.Life = AgentLifecycleState.Paused;
                a.Detail = "paused";
                evt = NewEvent("agent_state", agentId, "→Paused");
            }
        }
        if (evt is not null) EventReceived?.Invoke(evt);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ResumeAgentAsync(string agentId)
    {
        AgentEvent? evt = null;
        lock (_gate)
        {
            var a = Find(agentId);
            if (a.Life == AgentLifecycleState.Paused)
            {
                a.Life = a.LifeBeforePause == default ? AgentLifecycleState.Working : a.LifeBeforePause;
                a.Detail = "resumed";
                evt = NewEvent("agent_state", agentId, "Paused→Working");
            }
        }
        if (evt is not null) EventReceived?.Invoke(evt);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task EndAgentAsync(string agentId)
    {
        var raised = new List<AgentEvent>();
        lock (_gate)
        {
            var a = Find(agentId);
            if (a.Life is AgentLifecycleState.TornDown or AgentLifecycleState.Rejected) return Task.CompletedTask;
            a.Life = AgentLifecycleState.Rejected;
            SetMerge(a, WorkerMergeState.Rejected, raised);
            a.Detail = "ended — branch kept until teardown";
            a.Cooldown = 6;
            raised.Add(NewEvent("agent_state", agentId, "→Rejected (ended by user)"));
        }
        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.CompletedTask;
    }
    public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) { lock (_gate) return Find(agentId).Plan.ToList(); }

    // ---- IMergeQueueService -----------------------------------------------

    public string MainSha { get { lock (_gate) return _mainSha; } }

    public IReadOnlyList<QueueEntry> GetQueue()
    {
        lock (_gate)
            return _agents.Where(a => a.InQueue && a.Life != AgentLifecycleState.TornDown)
                .OrderBy(a => RailOrder(a.Merge))
                .Select(a => new QueueEntry(
                    a.Id, a.Name, a.Branch, a.Merge, a.Detail, a.Verification, a.Flagged.ToList(),
                    // The mock's Verifying rows are genuinely RUNNING — the tick loop advances them and
                    // their Detail counts "tests 12/41" as it goes. Letting this default to false would
                    // make the rail label every one of them "Stalled", badge it as a warning and offer
                    // "Clear stalled run" on a row visibly executing tests: the exact false claim about
                    // an entry's activity that the in-flight flag exists to prevent, merely inverted.
                    VerificationInFlight: a.Merge == WorkerMergeState.Verifying,
                    // A Dead agent's jail is gone; every other lifecycle state in the mock is one where a
                    // sandbox exists (TornDown agents are filtered out of the queue above). Answered rather
                    // than left null so the demo surface exercises the stranded shape at all — and answered
                    // from the agent's OWN lifecycle rather than hardcoded, so a mock agent that dies while
                    // the demo runs stops claiming a sandbox it does not have.
                    HasLiveSandbox: a.Life != AgentLifecycleState.Dead))
                .ToList();

        static int RailOrder(WorkerMergeState s) => s switch
        {
            WorkerMergeState.Verified => 0,
            WorkerMergeState.AwaitingReview => 0,
            WorkerMergeState.Verifying => 1,
            WorkerMergeState.Working => 2,
            WorkerMergeState.StaleVerified => 3,
            WorkerMergeState.Merged => 4,
            _ => 5,
        };
    }

    public bool CanMerge(string agentId, out string reason)
    {
        lock (_gate) return CanMergeCore(agentId, out reason);
    }

    /// <summary>Lock-held gate check — the UI renders <paramref name="reason"/> verbatim (§3.4 vocabulary).</summary>
    private bool CanMergeCore(string agentId, out string reason)
    {
        if (_frozen) { reason = "the queue is frozen — resume first"; return false; }
        var a = Find(agentId);
        if (a.Merge is not (WorkerMergeState.Verified or WorkerMergeState.AwaitingReview))
        { reason = a.Merge == WorkerMergeState.StaleVerified ? "verification is stale — re-verifying" : "not verified yet"; return false; }
        if (a.Verification is null || a.Verification.MainSha != _mainSha)
        { reason = "verification is stale — re-verifying"; return false; }
        var unacked = a.Flagged.Count(f => !f.Acknowledged);
        if (unacked > 0) { reason = $"{unacked} flagged item{(unacked == 1 ? "" : "s")} unacknowledged"; return false; }
        reason = "";
        return true;
    }

    /// <summary>
    /// The scripted stand-in for the daemon's verification trigger. The shipped app runs
    /// <see cref="Mainguard.Agents.UI"/>'s daemon-backed adapter, whose implementation is one RPC into
    /// <c>MergeQueue.RunVerificationAsync</c>; this exists so the design harness and the render harnesses
    /// drive the same seam. It refuses for the same reasons the daemon does (frozen queue, an entry that
    /// is already terminal) rather than pretending every request can run.
    /// </summary>
    public Task<VerificationOutcome> RunVerificationAsync(string agentId)
    {
        var raised = new List<AgentEvent>();
        VerificationOutcome outcome;
        lock (_gate)
        {
            if (_frozen)
            {
                return Task.FromResult(new VerificationOutcome(
                    Ran: false, Passed: false, Reason: "Can't verify — the queue is frozen; resume first."));
            }

            var a = Find(agentId);
            // Every terminal, Discarded included. Listing them was what let a discarded mock entry be
            // walked Discarded → Verifying → Verified, contradicting the terminal contract this mock
            // exists to stand in for.
            if (a.Merge is WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded)
            {
                return Task.FromResult(new VerificationOutcome(
                    Ran: false, Passed: false,
                    Reason: $"Can't verify — this branch is already {a.Merge.ToString().ToLowerInvariant()}."));
            }

            var now = DateTimeOffset.Now;
            SetMerge(a, WorkerMergeState.Verifying, raised);
            SetMerge(a, WorkerMergeState.Verified, raised);
            a.Life = AgentLifecycleState.AwaitingReview;
            a.Verification = new VerificationRecord(a.Id, _mainSha, true, a.TestsTotal, a.TestsTotal, now);
            a.Detail = "verified against " + _mainSha;
            _transcript.Add(new ChatLine(
                ChatLineKind.SystemLine, $"{a.Name} verified against {_mainSha} (requested)", now));
            outcome = new VerificationOutcome(Ran: true, Passed: true, Reason: "verified against main@" + _mainSha);
        }

        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.FromResult(outcome);
    }

    public Task<MergeOutcome> ConfirmMergeAsync(string agentId)
    {
        var raised = new List<AgentEvent>();
        MergeOutcome outcome;   // the mock spawns no external entries — every mock merge is a local one.
        lock (_gate)
        {
            if (!CanMergeCore(agentId, out var reason)) throw new InvalidOperationException($"Can't merge — {reason}.");
            var a = Find(agentId);
            SetMerge(a, WorkerMergeState.Merged, raised);
            a.Life = AgentLifecycleState.Merged;
            a.Detail = "merged"; a.Cooldown = 6;
            _mainSha = NewSha();
            raised.Add(NewEvent("merge_approved", agentId, "main@" + _mainSha));
            // NotifyMainMoved: the stale cascade (P2-10 step 3)
            foreach (var other in _agents.Where(x => x != a && x.Merge is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview))
            {
                SetMerge(other, WorkerMergeState.StaleVerified, raised);
                other.Detail = "re-verifying against " + _mainSha;
                other.Cooldown = 5 + _rng.Next(4);
            }
            _transcript.Add(new ChatLine(ChatLineKind.SystemLine, $"{a.Name} merged into main ({_mainSha}) — stale cascade re-queued {_agents.Count(x => x.Merge == WorkerMergeState.StaleVerified)} branch(es)", DateTimeOffset.Now));
            outcome = new MergeOutcome(MergeEntryOrigin.Local, agentId, "main", _mainSha);
        }
        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.FromResult(outcome);
    }

    /// <summary>
    /// Mock discard — the same shape the daemon enforces: terminal <c>Discarded</c> (never
    /// <c>Merged</c>), the entry leaves the live queue, and a terminal entry refuses.
    /// </summary>
    public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason)
    {
        var raised = new List<AgentEvent>();
        QueueEntryDiscardOutcome outcome;
        lock (_gate)
        {
            var a = Find(agentId);
            if (a.Merge is WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded)
            {
                throw new InvalidOperationException(
                    $"Can't discard — this entry is already {a.Merge} — a terminal entry cannot be discarded.");
            }

            SetMerge(a, WorkerMergeState.Discarded, raised);
            a.InQueue = false;
            a.Detail = string.IsNullOrWhiteSpace(reason) ? "discarded" : "discarded — " + reason;
            // "mock:" so nothing can mistake the mock's attribution for a daemon-derived identity.
            outcome = new QueueEntryDiscardOutcome(agentId, "mock:local", DateTimeOffset.Now);
        }

        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.FromResult(outcome);
    }

    /// <summary>
    /// Mock clear-stalled-verification. The mock has no real runs, so every <c>Verifying</c> entry here is
    /// by definition stalled; anything else refuses with the daemon's wording.
    /// </summary>
    public Task ClearStalledVerificationAsync(string agentId)
    {
        var raised = new List<AgentEvent>();
        lock (_gate)
        {
            var a = Find(agentId);
            if (a.Merge != WorkerMergeState.Verifying)
            {
                throw new InvalidOperationException(
                    $"Can't clear this verification — this entry is {a.Merge}, not stuck verifying — there is nothing to clear.");
            }

            SetMerge(a, WorkerMergeState.Working, raised);
            a.Detail = "verification cleared";
        }

        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mock resume — the same shape the daemon enforces: the entry is ADOPTED (same id, same branch, same
    /// row), an entry that already has a sandbox is refused, a terminal entry is refused, and a
    /// <c>Verifying</c> row with no run behind it is walked back to <c>Working</c> so it can be verified
    /// again. The mock has no jails, so "gets a sandbox" is the agent returning to a live lifecycle state.
    /// </summary>
    public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind)
    {
        var raised = new List<AgentEvent>();
        QueueEntryResumeOutcome outcome;
        lock (_gate)
        {
            var a = Find(agentId);
            if (a.Merge is WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded)
            {
                throw new InvalidOperationException(
                    $"Can't resume — this entry is already {a.Merge} — a terminal entry has nothing left to resume.");
            }

            if (a.Life != AgentLifecycleState.Dead)
            {
                throw new InvalidOperationException(
                    "Can't resume — this entry already has a live agent — stop it before resuming.");
            }

            var clearedStalled = a.Merge == WorkerMergeState.Verifying;
            if (clearedStalled)
            {
                SetMerge(a, WorkerMergeState.Working, raised);
            }

            a.Life = AgentLifecycleState.Working;
            a.Detail = "resumed on " + a.Branch;
            outcome = new QueueEntryResumeOutcome(agentId, a.Branch, a.Merge, clearedStalled);
        }

        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.FromResult(outcome);
    }

    public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId)
    {
        lock (_gate)
        {
            var a = Find(agentId);
            var i = a.Flagged.FindIndex(f => f.Id == itemId);
            if (i >= 0) a.Flagged[i] = a.Flagged[i] with { Acknowledged = true };
        }
        EventReceived?.Invoke(NewEvent("acknowledged_flagged_change", agentId, itemId));
        return Task.CompletedTask;
    }

    // ---- ICoordinatorService ----------------------------------------------

    public IReadOnlyList<ChatLine> GetTranscript() { lock (_gate) return _transcript.ToList(); }
    public IReadOnlyList<TaskPlan> GetPendingPlans() { lock (_gate) return _pendingPlans.ToList(); }
    public TaskPlan? GetPlan(string planId) { lock (_gate) return _pendingPlans.FirstOrDefault(p => p.PlanId == planId); }

    public Task SendAsync(string text)
    {
        lock (_gate)
        {
            _transcript.Add(new ChatLine(ChatLineKind.Human, text, DateTimeOffset.Now));
            _transcript.Add(new ChatLine(ChatLineKind.Coordinator,
                "Noted. I'll fold that into the running work — nothing needs a new plan yet.", DateTimeOffset.Now));
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task SubmitPlanDecisionAsync(string planId, bool approve)
    {
        var raised = new List<AgentEvent>();
        lock (_gate)
        {
            var plan = _pendingPlans.FirstOrDefault(p => p.PlanId == planId);
            if (plan is null) return Task.CompletedTask;
            _pendingPlans.Remove(plan);
            if (approve)
            {
                raised.Add(NewEvent("plan_decided", "coordinator", $"{planId}=approved"));
                var a = new AgentState
                {
                    Id = "loom-5",
                    Name = "Loom-5",
                    Branch = "fix/token-expiry",
                    Life = AgentLifecycleState.Provisioning,
                    Merge = WorkerMergeState.Working,
                    Detail = "provisioning sandbox…",
                    SpawnedAt = DateTimeOffset.Now,
                    Cooldown = 3,
                    Plan = plan.Scope.Select(s => ("Touch " + s, false)).ToList(),
                };
                _agents.Add(a);
                _transcript.Add(new ChatLine(ChatLineKind.SystemLine, $"Plan #7 approved — Loom-5 spawned on fix/token-expiry", DateTimeOffset.Now));
                raised.Add(NewEvent("agent_state", a.Id, "Requested→Provisioning"));
            }
            else
            {
                raised.Add(NewEvent("plan_decided", "coordinator", $"{planId}=rejected"));
                _transcript.Add(new ChatLine(ChatLineKind.SystemLine, "Plan #7 rejected — no worker spawned, no worktree created", DateTimeOffset.Now));
            }
        }
        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    // ---- IKillSwitchService -------------------------------------------------

    public bool IsFrozen { get { lock (_gate) return _frozen; } }
    public KillSwitchPhase Phase { get { lock (_gate) return _phase; } }

    public string PhaseText
    {
        get
        {
            lock (_gate)
            {
                var paused = _agents.Count(a => a.Life == AgentLifecycleState.Paused);
                var running = _agents.Count(a => a.Life is not (AgentLifecycleState.TornDown or AgentLifecycleState.Merged or AgentLifecycleState.Rejected));
                return _phase switch
                {
                    KillSwitchPhase.QueueFrozen or KillSwitchPhase.PerAgentYield => $"queue frozen · {paused} of {running} agents paused",
                    KillSwitchPhase.Frozen or KillSwitchPhase.Snapshotted => $"queue frozen · {paused} of {running} agents paused · snapshotting…",
                    KillSwitchPhase.Complete => $"queue frozen · {paused} agents paused · snapshot saved",
                    _ => "",
                };
            }
        }
    }

    public Task EngageAsync()
    {
        List<AgentEvent> raised = new();
        lock (_gate)
        {
            if (_frozen) return Task.CompletedTask;
            _frozen = true;                       // freeze FIRST — instant, before any yield (OPS §4.5 / SA-1)
            _phase = KillSwitchPhase.QueueFrozen;
            _phaseTicks = 0;
            raised.Add(NewEvent("killswitch", "*", "phase=QueueFrozen"));
        }
        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        List<AgentEvent> raised = new();
        lock (_gate)
        {
            if (!_frozen) return Task.CompletedTask;
            _frozen = false;
            _phase = KillSwitchPhase.Armed;
            foreach (var a in _agents.Where(x => x.Life == AgentLifecycleState.Paused))
            {
                a.Life = a.LifeBeforePause == default ? AgentLifecycleState.Working : a.LifeBeforePause;
                a.Detail = "resumed";
                raised.Add(NewEvent("agent_state", a.Id, "Paused→Working"));
            }
        }
        foreach (var e in raised) EventReceived?.Invoke(e);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    // ---- ITelemetryService --------------------------------------------------

    public IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null)
    {
        lock (_gate)
            return (agentId is null ? _sandbox : _sandbox.Where(e => e.AgentId == agentId)).OrderByDescending(e => e.At).ToList();
    }

    public ResourceSample Current { get { lock (_gate) return _samples[^1]; } }

    public IReadOnlyList<AgentResourceUsage> GetAgentUsage()
    {
        lock (_gate)
            return _agents.Where(a => a.Life != AgentLifecycleState.TornDown)
                // IsMetered: true — the scripted fleet models a BYOK deployment (it also carries a
                // scripted $50/day cap), so the design harnesses keep exercising the full cost UI.
                // Stated explicitly rather than left to default false, which would silently switch
                // every design capture to the "spend isn't tracked" surface.
                .Select(a => new AgentResourceUsage(
                    a.Id, a.Name, a.Life.ToString(), a.Life == AgentLifecycleState.Paused,
                    Math.Round(a.Cpu, 1), Math.Round(a.Ram, 2), Math.Round(a.Spend, 2), a.Detail,
                    IsMetered: true))
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
    }
    public IReadOnlyList<ResourceSample> History { get { lock (_gate) return _samples.ToList(); } }

    // Scripted budget: a representative $50/day cap so the Resource Monitor's editor has something to show
    // and round-trip in the design harness (no daemon behind it).
    private SpendBudget _budget = new(PerAgentUsdMicrosCap: 0, PerAgentTokenCap: 0,
        PerDayUsdMicrosCap: 50_000_000, PerDayTokenCap: 0);

    public Task<SpendBudget> GetSpendBudgetAsync(System.Threading.CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_budget);
    }

    public Task SetSpendBudgetAsync(SpendBudget budget, System.Threading.CancellationToken ct = default)
    {
        lock (_gate) _budget = budget;
        return Task.CompletedTask;
    }

    // ---- IVibeService ---------------------------------------------------------

    public IReadOnlyList<Checkpoint> GetCheckpoints() { lock (_gate) return _checkpoints.ToList(); }
    public Checkpoint? LastVerifiedGreen { get { lock (_gate) return _checkpoints.LastOrDefault(c => c.VerifiedGreen); } }

    public Task RestoreCheckpointAsync(string sha)
    {
        lock (_gate)
            _checkpoints.Add(new Checkpoint(NewSha(), "Went back to an earlier saved point", DateTimeOffset.Now, true));
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public DeployStatus Deploy { get { lock (_gate) return _deploy; } }

    public Task PublishAsync()
    {
        lock (_gate)
        {
            _deploy = new DeployStatus(DeployPhase.Saving, null, null);
            _deployTicks = 0;
        }
        DeployChanged?.Invoke();
        return Task.CompletedTask;
    }

    // ---- plumbing -------------------------------------------------------------

    private AgentState Find(string agentId)
        => _agents.FirstOrDefault(a => a.Id == agentId) ?? throw new KeyNotFoundException($"No agent '{agentId}'.");

    private string NewSha()
    {
        const string hex = "0123456789abcdef";
        Span<char> c = stackalloc char[7];
        for (int i = 0; i < 7; i++) c[i] = hex[_rng.Next(16)];
        return new string(c);
    }

    public void Dispose() => _timer.Dispose();
}
