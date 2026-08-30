using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The persistence seam for <see cref="PlanModeSwitch"/> (daemon-side, restart-safe).</summary>
public interface IPlanModeStore
{
    /// <summary>The persisted setting, or <c>null</c> when nothing has been persisted (or it is unreadable).</summary>
    bool? Load();

    /// <summary>Persist the setting.</summary>
    void Save(bool enabled);
}

/// <summary>An <see cref="IPlanModeStore"/> that forgets on restart. The default in tests.</summary>
public sealed class InMemoryPlanModeStore : IPlanModeStore
{
    private bool? _value;

    public InMemoryPlanModeStore(bool? seed = null) => _value = seed;

    public bool? Load() => _value;

    public void Save(bool enabled) => _value = enabled;
}

/// <summary>
/// A one-file JSON <see cref="IPlanModeStore"/>, written beside the plan store.
///
/// <para>Deliberately NOT an EF row. The setting belongs to the plan subsystem, whose own record
/// (<c>mainguard-plans.json</c>) is already a JSON file in the same directory for the same reason: the
/// daemon must be able to answer "is the gate on?" before anything that needs a database is up, and a
/// migration for one boolean buys nothing.</para>
///
/// <para><b>Every failure reads as "nothing persisted", never as "off".</b> A missing, truncated or
/// hand-mangled file must not be able to disable a human approval gate — see
/// <see cref="PlanModeSwitch"/>'s fail-closed default.</para>
/// </summary>
public sealed class JsonPlanModeStore : IPlanModeStore
{
    private readonly string _path;

    public JsonPlanModeStore(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    private sealed record Document(bool Enabled);

    public bool? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            return doc?.Enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(bool enabled)
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(new Document(enabled)));
    }
}

/// <summary>
/// The operator's <b>plan-mode toggle</b>: whether a coordinator-delegated worker must have a
/// human-approved plan before it is given its task.
///
/// <para><b>On</b> (the default) is the phase-2 model unchanged — the daemon withholds the task, the
/// worker authors a plan against its brief, presents it and blocks, and a human decides
/// (<c>coordinator-contract.md</c> §2). <b>Off</b> is the mode the owner asked for: the worker is a
/// delegated worker in every other respect and simply receives its task at spawn, with no plan and no
/// approval.</para>
///
/// <para><b>Why this is a daemon-side object and not a client preference.</b> The gate it governs is
/// enforced where the daemon serves the call, so the switch has to live on the same side of the wire as
/// the thing it switches. A client-held preference would be a value the enforcement point never reads —
/// which is precisely the shape of control this codebase keeps finding to be decorative (MG-12). The
/// client sets it over <c>PlanApprovalService.SetPlanMode</c>, which the <c>RoleInterceptor</c> denies to
/// a coordinator credential for the same reason it denies <c>ApprovePlan</c>: an agent that could turn the
/// gate off would hold the gate it is denied at.</para>
///
/// <para><b>Read once per spawn, never re-read afterwards.</b> A worker's mode is fixed at
/// <see cref="WorkerPlanGate.Hold"/> and travels with its held task. Toggling the switch therefore governs
/// the NEXT spawn and never retroactively authorises a worker that is already blocked at the gate, or
/// retroactively blocks one that has already been told to start working. A live re-read would do both,
/// and each direction is a different lie: the first is an approval nobody gave, the second is a worker
/// stranded mid-task by a preference change.</para>
/// </summary>
public sealed class PlanModeSwitch
{
    /// <summary>
    /// What the switch is when nothing has ever been persisted, or the store cannot be read.
    ///
    /// <para><b>On.</b> The default of a human-approval gate is that it is there. An unreadable settings
    /// file must not be a way to remove one.</para>
    /// </summary>
    public const bool DefaultEnabled = true;

    private readonly IPlanModeStore _store;
    private readonly IAuditLog _audit;
    private readonly object _gate = new();
    private bool _enabled;

    public PlanModeSwitch(IPlanModeStore? store = null, IAuditLog? audit = null)
    {
        _store = store ?? new InMemoryPlanModeStore();
        _audit = audit ?? new InMemoryAuditLog();
        _enabled = _store.Load() ?? DefaultEnabled;
    }

    /// <summary>Whether a delegated worker must have an approved plan before it is given its task.</summary>
    public bool Enabled
    {
        get { lock (_gate) { return _enabled; } }
    }

    /// <summary>The mode a worker spawned right now would be held under.</summary>
    public WorkerPlanMode ModeForNewWorker => Enabled ? WorkerPlanMode.Gated : WorkerPlanMode.Ungated;

    /// <summary>Raised (off the lock) when the setting actually changes.</summary>
    public event Action<bool>? Changed;

    /// <summary>
    /// Sets the switch, records it, and returns true when the value actually moved.
    ///
    /// <para>The audit record is the point. Turning a human-approval gate off is exactly the kind of act
    /// that must be reconstructable later — <see cref="WorkerPlanGate.MergeEvidence"/> says it again on
    /// every merge record of a worker spawned while it was off, so the two halves agree.</para>
    /// </summary>
    /// <param name="actor">Who asked (a daemon-derived identity — never a client-supplied string).</param>
    public bool Set(bool enabled, string actor = "")
    {
        bool changed;
        lock (_gate)
        {
            changed = _enabled != enabled;
            _enabled = enabled;
        }

        // Persisted even when unchanged: the first Set after a fresh install is what turns the in-memory
        // default into a recorded decision, and that is worth having on disk.
        try
        {
            _store.Save(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _audit.Append(new AuditEvent("plan_mode_persist_failed", new Dictionary<string, string>
            {
                ["enabled"] = enabled ? "true" : "false",
                ["reason"] = ex.Message,
            }));
        }

        if (!changed)
        {
            return false;
        }

        _audit.Append(new AuditEvent("plan_mode_changed", new Dictionary<string, string>
        {
            ["enabled"] = enabled ? "true" : "false",
            ["actor"] = actor ?? string.Empty,
        }));
        Changed?.Invoke(enabled);
        return true;
    }

    /// <summary>
    /// The one sentence a human reads to know whether the gate is on. Rendered here rather than in the
    /// client for the standing reason: a second copy of a sentence is a copy that goes stale, and a
    /// surface that disagrees with its gate is how somebody comes to believe they have an approval step
    /// they do not have.
    /// </summary>
    public string Summary => Enabled
        ? "Plan mode is ON — every worker authors a plan and blocks until you approve it."
        : "Plan mode is OFF — workers receive their task at spawn and start implementing immediately. "
          + "No plan is authored and nothing waits for your approval.";
}
