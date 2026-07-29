using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// MG-10 — the swarm-up lifecycle hook that actually BUILDS a repo's <see cref="MergeQueue"/> and puts it
/// in the <see cref="MergeQueueRegistry"/>.
///
/// <para><b>Why this type exists.</b> Every ingredient of the P2-10 merge contract shipped — the state
/// machine, the immutable verification store, the RT-D1 lease, the stale cascade, the RT-D2 provenance
/// resolver — and not one of them was ever instantiated outside the test projects. The daemon registered an
/// <i>empty</i> <see cref="IMergeQueueRegistry"/> and nothing called <c>Register</c>, so every
/// <c>MergeQueueService</c> RPC resolved to <c>NOT_FOUND</c> for the daemon's whole lifetime and the client's
/// queue pump swallowed it and retried against an empty projection. The merge guarantees were therefore
/// neither enforced nor bypassable — they simply were not running. This is the missing constructor call,
/// placed on the events that make a repo "active": the repo is provisioned, or an agent joins it.</para>
///
/// <para><b>Restart resume comes free:</b> <see cref="MergeQueue"/> hydrates from its persisted rows in its
/// constructor, so a queue rebuilt here after a daemon restart resumes the repo's real state rather than
/// starting empty — which is exactly why the queue must be rebuilt from the SAME persisted stores the
/// previous daemon instance wrote to, not from fresh in-memory ones.</para>
/// </summary>
public sealed class MergeQueueProvisioner
{
    /// <summary>
    /// The tracked, in-tree file whose content is the repo's verification command line (P2-10 §3.2 "the
    /// project's configured verification command"). It is read out of git — <c>git show &lt;ref&gt;:</c> —
    /// for BOTH the branch and the main baseline, which is what makes RT-D2 drift detectable at all: the
    /// comparison is between two committed trees, so a branch that rewrites its own test command to
    /// <c>exit 0</c> differs from main's baseline and is flagged before a merge is possible.
    /// </summary>
    public const string VerificationConfigPath = ".mainguard/verify";

    /// <summary>
    /// The tracked, in-tree file naming the toolchains the verification command needs inside the jail
    /// (<see cref="RepoToolchainConfig.Path"/>). Read out of git for BOTH trees, exactly like
    /// <see cref="VerificationConfigPath"/> — but with one asymmetry the verify command does not have:
    /// only main's copy is ever <i>provisioned</i>. See <see cref="ToolchainDeclarationResolver"/>.
    /// </summary>
    public const string ToolchainConfigPath = RepoToolchainConfig.Path;

    private readonly object _gate = new();
    private readonly MergeQueueRegistry _registry;
    private readonly IRepoProvisioner _repos;
    private readonly IMergeLeaseStore _leases;
    private readonly Func<string, string, string?> _resolveContainerId;
    private readonly Func<string, IMergeQueueStore> _queueStore;
    private readonly Func<string, IVerificationStore> _verificationStore;
    private readonly ISandboxEngine _sandboxes;
    private readonly VerificationRunner _runner;
    private readonly IAuditLog _audit;
    private readonly Action<string>? _log;
    private readonly Func<string, string, bool>? _publishAgentRef;

    /// <param name="registry">The registry the gRPC layer resolves repo handles through.</param>
    /// <param name="repos">Locates the daemon-side bare mirror for a repo hash (main sha + config trees).</param>
    /// <param name="leases">The RT-D1 merge-lease store. <b>Must be the same singleton</b> the foreground
    /// merge, <c>BeginMerge</c> and <see cref="MergeDispatch"/> use — the one-outstanding-merge-per-repo
    /// invariant is only an invariant while every origin contends for the same store (MG-23).</param>
    /// <param name="resolveContainerId">(repoHash, agentId) → the agent's live jail, or null when it has
    /// none. Verification runs in the worker's own sandbox; no jail means no verification (never the host).</param>
    /// <param name="queueStore">repoHash → the persisted queue-state store (SQLite in the daemon).</param>
    /// <param name="verificationStore">repoHash → the immutable verification-record store.</param>
    /// <param name="sandboxes">The engine that runs the test command and reports the container-runtime exit.</param>
    /// <param name="artifactDirectory">Daemon-owned directory the verification log artifacts land in.</param>
    /// <param name="audit">Audit sink threaded into every queue (the loud <c>stale_override_used</c> path).</param>
    /// <param name="log">Optional milestone sink (daemon Merge log category).</param>
    /// <param name="publishAgentRef">
    /// MG-3 — (repoHash, agentId) → carry the agent's branch from its own repository into the mirror.
    /// Called immediately BEFORE every verification, which is the second half of the resolved
    /// fetch-trigger question (design §7): the watcher keeps the mirror responsive, and this makes the
    /// bytes that get verified definitely current rather than whatever the watcher last saw. Null (the
    /// pre-MG-3 tests) simply verifies whatever the mirror already holds.
    /// </param>
    public MergeQueueProvisioner(
        MergeQueueRegistry registry,
        IRepoProvisioner repos,
        IMergeLeaseStore leases,
        Func<string, string, string?> resolveContainerId,
        Func<string, IMergeQueueStore> queueStore,
        Func<string, IVerificationStore> verificationStore,
        ISandboxEngine sandboxes,
        string artifactDirectory,
        IAuditLog? audit = null,
        Action<string>? log = null,
        Func<string, string, bool>? publishAgentRef = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _resolveContainerId = resolveContainerId ?? throw new ArgumentNullException(nameof(resolveContainerId));
        _queueStore = queueStore ?? throw new ArgumentNullException(nameof(queueStore));
        _verificationStore = verificationStore ?? throw new ArgumentNullException(nameof(verificationStore));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _runner = new VerificationRunner(
            _sandboxes,
            artifactDirectory ?? throw new ArgumentNullException(nameof(artifactDirectory)));
        _audit = audit ?? new InMemoryAuditLog();
        _log = log;
        _publishAgentRef = publishAgentRef;
    }

    /// <summary>
    /// Ensures a live, registered queue for <paramref name="repoHandle"/> and returns it; null when the repo
    /// has no provisioned bare mirror yet (nothing to govern — the same "empty until a repo is active"
    /// posture the registry has always documented, now with an actual path out of "empty").
    ///
    /// <para>Idempotent, and safe to call on every provision/spawn. On a repeat call it also RECONCILES the
    /// queue's authoritative <c>main@sha</c> with the mirror's: a re-provision fetches main forward, and a
    /// queue still comparing verifications against the pre-fetch sha would call stale evidence fresh. Moving
    /// it fires the ordinary stale cascade, which is the correct response to main having moved.</para>
    /// </summary>
    public MergeQueueContext? EnsureQueue(string repoHandle)
    {
        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return null;
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);
        var mainSha = RevParse(barePath, mainBranch);
        if (string.IsNullOrEmpty(mainSha))
        {
            // No mirror (or an empty one): a queue keyed to an unknown main would verify against "" and
            // call every record fresh. Better to stay absent — the handle keeps resolving to NOT_FOUND,
            // which is honest, until the repo is really provisioned.
            return null;
        }

        MergeQueueContext context;
        var created = false;
        lock (_gate)
        {
            var existing = _registry.Resolve(repoHandle);
            if (existing is not null)
            {
                context = existing;
            }
            else
            {
                context = Build(repoHandle, mainSha);
                _registry.Register(repoHandle, context);
                created = true;
            }
        }

        if (!created && !string.Equals(context.Queue.CurrentMainSha, mainSha, StringComparison.Ordinal))
        {
            context.Queue.NotifyMainMoved(mainSha);
            _log?.Invoke($"merge queue repo={repoHandle} main advanced to {mainSha} — stale cascade fired");
        }

        if (created)
        {
            _log?.Invoke($"merge queue registered repo={repoHandle} main={mainSha} branch={mainBranch}");
        }

        return context;
    }

    /// <summary>
    /// Ensures the repo's queue exists AND that <paramref name="agentId"/> has an entry in it (the agent
    /// joined the swarm). Without this an agent's branch is invisible to <c>StreamQueue</c>: the queue only
    /// reports agents it tracks, and nothing else in the daemon ever adds one.
    /// </summary>
    public void EnsureEntry(string repoHandle, string agentId, MergeEntryOrigin origin = MergeEntryOrigin.Local)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        EnsureQueue(repoHandle)?.Queue.EnsureEntry(agentId, origin);
    }

    /// <summary>Drops a repo's queue on teardown (the handle resolves to NOT_FOUND again).</summary>
    public void Remove(string repoHandle) => _registry.Remove(repoHandle);

    // ---- Internals -------------------------------------------------------

    private MergeQueueContext Build(string repoHandle, string mainSha)
    {
        // The RT-D2 gate is per-repo-queue because its flag state is per-branch and its acknowledgment is
        // the human's; sharing one across repos would let one repo's ack clear another's flag.
        var changedTestCommand = new ChangedTestCommandGate();

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: repoHandle,
            currentMainSha: mainSha,
            store: _queueStore(repoHandle),
            verifications: _verificationStore(repoHandle),
            runVerification: (agentId, ct) => RunVerificationAsync(repoHandle, agentId, queue, changedTestCommand, ct),
            // Null requeue = re-verify, which is the production stale-cascade behaviour (§3.3): a staled
            // branch re-runs its own verification against the new main rather than sitting stale forever.
            requeue: null,
            gates: new IMergeGate[] { changedTestCommand },
            audit: _audit);

        return new MergeQueueContext(queue, _leases) { ChangedTestCommand = changedTestCommand };
    }

    /// <summary>
    /// One verification run, daemon-side: resolve the RT-D2 command provenance from git, record whether it
    /// drifted from the main baseline, then run it in the agent's OWN jail and let the container-runtime
    /// exit decide (OPS SA-1 — never a supervisor-reported frame).
    ///
    /// <para>MG-11 (2): <see cref="VerificationCommandResolver"/> had no production caller at all, so
    /// <c>ChangedVsMain</c> was never computed in the running app and the <c>changed-test-command</c> flag
    /// could not fire — a branch really could rewrite its own test command and merge unflagged. Resolving it
    /// HERE, at verification time, is also what pins the provenance into the immutable
    /// <see cref="VerificationRecord"/> the queue persists.</para>
    /// </summary>
    private async Task<VerificationRecord> RunVerificationAsync(
        string repoHandle, string agentId, MergeQueue queue, ChangedTestCommandGate changedGate, CancellationToken ct)
    {
        var containerId = _resolveContainerId(repoHandle, agentId);
        if (string.IsNullOrEmpty(containerId))
        {
            // Host execution is a rejection trigger (§3.2). No jail ⇒ no verification, loudly.
            throw new InvalidOperationException(
                $"Agent '{agentId}' has no live sandbox — verification runs in the worker's own jail, never on the host.");
        }

        // MG-3 (design §7, "fetch trigger: both"): re-publish the agent's branch from its OWN repository
        // into the mirror right now, so what is about to be verified is the agent's current tip rather
        // than whatever the ref watcher last happened to see. The daemon names the source ref and the
        // destination; the agent cannot name a ref at all.
        _publishAgentRef?.Invoke(repoHandle, agentId);

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);
        var resolution = VerificationCommandResolver.Resolve(
            branchConfigContent: ShowFile(barePath, "agent/" + agentId, VerificationConfigPath),
            mainConfigContent: ShowFile(barePath, mainBranch, VerificationConfigPath));

        // The same RT-D2 question asked of the TOOLCHAIN declaration. Note the argument order is
        // identical to the line above — branch vs main — but the resolver's answer is not symmetric:
        // what it hands back to provision is always main's, and the branch's copy only decides the flag.
        var toolchain = ToolchainDeclarationResolver.Resolve(
            branchConfigContent: ShowFile(barePath, "agent/" + agentId, ToolchainConfigPath),
            mainConfigContent: ShowFile(barePath, mainBranch, ToolchainConfigPath),
            repoHandle: repoHandle);

        // Arm (or clear) the RT-D2 gate BEFORE the run: a branch whose command drifted is unmergeable from
        // the moment we know, not from whenever a UI happens to look.
        changedGate.SetFlagged(agentId, ChangedTestCommandGate.TestCommandItem, resolution.ChangedVsMain);
        changedGate.SetFlagged(agentId, ChangedTestCommandGate.ToolchainItem, toolchain.ChangedVsMain);

        // ...and before running anything, confirm the jail REALLY carries what main declared. This is a
        // daemon-observed exec in the worker's own container, not a lookup in the daemon's own
        // bookkeeping: the failure being defended against is precisely the one where our records say the
        // layer was provisioned and the container is running something else. Without it, a jail missing
        // its toolchain produces exit 127 and an ordinary "verification failed" — indistinguishable from
        // the agent's code being broken, on the one screen where that distinction decides a merge.
        await EnsureToolchainPresentAsync(repoHandle, containerId!, toolchain.Provisioned, ct).ConfigureAwait(false);

        // Pin the record to the queue's authoritative main — the same value CanMerge compares against, so a
        // pass here is a pass against the main this branch will actually merge into.
        return await _runner.RunAsync(
            new VerificationRequest(
                agentId, containerId!, queue.CurrentMainSha,
                resolution.Command, resolution.ResolvedCommand, resolution.ConfigHash),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs each declared toolchain's catalogued probe inside the worker's jail. Every probe must exit
    /// zero; the first that does not raises <see cref="ToolchainProvisioningException"/> and the
    /// verification never runs.
    /// </summary>
    private async Task EnsureToolchainPresentAsync(
        string repoHandle, string containerId, ToolchainDeclaration declaration, CancellationToken ct)
    {
        if (declaration.IsEmpty)
        {
            return;
        }

        foreach (var id in declaration.Ids)
        {
            var recipe = ToolchainCatalog.TryGet(id)
                ?? throw new UnknownToolchainException(repoHandle, id, ToolchainCatalog.KnownIds);

            SandboxExecResult probe;
            try
            {
                probe = await _sandboxes.ExecAsync(containerId, recipe.Probe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                    $"could not probe '{id}' in the worker's jail: {ex.Message}");
            }

            if (probe.ExitCode != 0)
            {
                throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                    $"'{id}' is declared but absent from the worker's jail — the probe "
                    + $"`{string.Join(' ', recipe.Probe)}` exited {probe.ExitCode}. "
                    + "Verification was NOT run; this is a provisioning failure, not a failing test. "
                    + $"stderr: {Trim(probe.Stderr)}");
            }
        }
    }

    private static string Trim(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        return s.Length <= 400 ? s : s[^400..];
    }

    // `git show <ref>:<path>` — null when the ref or the path is absent (an absent branch-side config is
    // the typed "no verification command configured" edge; an absent main-side baseline counts as drift).
    private static string? ShowFile(string barePath, string reference, string path)
        => TryGit(barePath, out var output, "show", $"{reference}:{path}") ? output : null;

    private static string RevParse(string barePath, string reference)
        => TryGit(barePath, out var output, "rev-parse", "--verify", reference) ? output.Trim() : string.Empty;

    private static string ResolveDefaultBranch(string barePath)
    {
        if (TryGit(barePath, out var output, "symbolic-ref", "--short", "HEAD"))
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    // The mirror directory is checked before every spawn: git's working directory must exist or
    // Process.Start throws, and "this repo was never provisioned" is a routine state here (the provisioner
    // is called on spawn paths that legitimately degrade to session-only), not an error.
    private static bool TryGit(string barePath, out string output, params string[] args)
    {
        output = string.Empty;
        if (!System.IO.Directory.Exists(barePath))
        {
            return false;
        }

        return AgentGitCommand.TryRun(barePath, out output, args) == 0;
    }
}
