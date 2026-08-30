using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Git.Review;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>What one verification run needs: the sandbox to run in, the <b>two</b> shas to pin, and the
/// RT-D2-resolved command + config hash to record.</summary>
/// <param name="BranchSha">The mirror's <c>refs/heads/agent/&lt;id&gt;</c> tip this run is measured ON,
/// resolved after the pre-verification publish so it is the tree the container actually holds. Empty
/// where the caller cannot resolve one (the seeded path); see <see cref="VerificationRecord.BranchSha"/>
/// for why empty means "not measured" and never "unchanged".</param>
public sealed record VerificationRequest(
    string AgentId,
    string ContainerId,
    string MainSha,
    IReadOnlyList<string> Command,
    string ResolvedCommand,
    string ConfigHash,
    string BranchSha = "");

/// <summary>
/// Runs a project's configured verification command <b>in the worker's own sandbox</b> and returns the
/// immutable <see cref="VerificationRecord"/>. Host execution is a rejection trigger — the command runs
/// via <see cref="ISandboxEngine.ExecAsync"/> (cwd = the worktree inside the container).
///
/// <para><b>OPS SA-1 (decision D — binding).</b> Pass/fail is the <b>daemon-observed container-runtime
/// exit code</b> that <see cref="ISandboxEngine.ExecAsync"/> reads from containerd, OUTSIDE the
/// in-sandbox <c>AgentSupervisor</c>'s control. It is NEVER a value reported over the OOB
/// <c>VerifyResult</c> frame: the supervisor is not in the TCB, and a compromised one could otherwise
/// forge a <c>Verified</c> state — the single trust-ending forgery. This runner does not read, accept,
/// or even reference a supervisor-reported result.</para>
///
/// <para><b>Honest residual.</b> The test still executes against agent-controlled files/deps, so a
/// poisoned test <i>runner</i> can exit 0 over failing code (RT-D2 flags command-<i>text</i> changes,
/// not the execution closure). <c>Verified</c> is therefore a <b>quality signal, not a security boundary
/// against an adversarial branch</b>; the gate against hostile code is human diff review (P2-11).</para>
/// </summary>
public sealed class VerificationRunner
{
    private readonly ISandboxEngine _sandbox;
    private readonly string _artifactDir;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="sandbox">The engine that launches <c>docker exec</c> and reports the container-runtime exit.</param>
    /// <param name="artifactDir">Directory the full log artifact is captured to (daemon-owned).</param>
    /// <param name="clock">Injectable clock (tests use a virtual one).</param>
    public VerificationRunner(ISandboxEngine sandbox, string artifactDir, Func<DateTimeOffset>? clock = null)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _artifactDir = artifactDir ?? throw new ArgumentNullException(nameof(artifactDir));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Runs the command in the agent's container and records the daemon-observed result.</summary>
    public async Task<VerificationRecord> RunAsync(VerificationRequest request, CancellationToken ct)
    {
        if (request.Command is null || request.Command.Count == 0)
        {
            throw new NoVerificationCommandException("No verification command configured for this repository.");
        }

        // The ONLY source of pass/fail: the container-runtime exit reported by the sandbox engine.
        var result = await _sandbox.ExecAsync(request.ContainerId, request.Command, ct).ConfigureAwait(false);
        var passed = result.ExitCode == 0;

        var when = _clock();
        var artifactPath = WriteArtifact(request, result, when);

        return new VerificationRecord(
            request.AgentId,
            request.MainSha,
            passed,
            artifactPath,
            request.ResolvedCommand,
            request.ConfigHash,
            when,
            request.BranchSha);
    }

    private string WriteArtifact(VerificationRequest request, SandboxExecResult result, DateTimeOffset when)
    {
        Directory.CreateDirectory(_artifactDir);
        var name = $"verify_{Sanitize(request.AgentId)}_{when.UtcDateTime:yyyyMMddTHHmmssfff}_{request.MainSha}.log";
        var path = Path.Combine(_artifactDir, name);

        var sb = new StringBuilder();
        sb.AppendLine($"agent: {request.AgentId}");
        sb.AppendLine($"main@sha: {request.MainSha}");
        // BOTH shas in the header, because the verdict below is only true between them. A log naming one
        // of the pair reads as a statement about the branch when it is a statement about a moment.
        sb.AppendLine($"branch@sha: {(request.BranchSha.Length > 0 ? request.BranchSha : "(not measured)")}");
        sb.AppendLine($"resolved-command: {request.ResolvedCommand}");
        sb.AppendLine($"config-hash: {request.ConfigHash}");
        sb.AppendLine($"container-runtime-exit: {result.ExitCode}");
        sb.AppendLine($"when-utc: {when.UtcDateTime:O}");
        sb.AppendLine("---- stdout ----");
        sb.AppendLine(result.Stdout);
        sb.AppendLine("---- stderr ----");
        sb.AppendLine(result.Stderr);

        try
        {
            File.WriteAllText(path, sb.ToString());
        }
        catch (IOException)
        {
            // The artifact is best-effort; a write failure must not lose the daemon-observed verdict.
        }

        return path;
    }

    private static string Sanitize(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}

/// <summary>
/// RT-D2 verification-command provenance resolver (contract §3.8). Resolves the test command from the
/// <b>main-side baseline</b> and compares it to the branch-side config; a change in the command text or
/// its defining config becomes a must-acknowledge <c>changed-test-command</c> flagged item before a
/// merge is possible — a branch cannot self-green by rewriting its test to <c>exit 0</c>.
/// </summary>
public static class VerificationCommandResolver
{
    /// <summary>The resolved command plus whether it drifted from the main-side baseline.</summary>
    public sealed record Resolution(
        IReadOnlyList<string> Command,
        string ResolvedCommand,
        string ConfigHash,
        bool ChangedVsMain);

    /// <summary>
    /// Resolves the command. A human-owned <paramref name="pinnedCommand"/> (optional per-repo setting)
    /// overrides branch-side config entirely and is never flagged. Otherwise the branch-side config
    /// defines the command; if it differs from the main-side baseline config it is flagged.
    /// </summary>
    /// <param name="branchConfigContent">The verification-config file content from the branch tree (null = absent).</param>
    /// <param name="mainConfigContent">The verification-config file content from the main baseline tree (null = absent).</param>
    /// <param name="pinnedCommand">An out-of-branch, human-owned command pin (null = none).</param>
    public static Resolution Resolve(string? branchConfigContent, string? mainConfigContent, string? pinnedCommand = null)
    {
        if (!string.IsNullOrWhiteSpace(pinnedCommand))
        {
            var pinnedArgv = Tokenize(pinnedCommand!);
            RejectSurvivingShellOperators(pinnedArgv, pinnedCommand!.Trim());
            return new Resolution(pinnedArgv, pinnedCommand!.Trim(), Sha256(pinnedCommand!), ChangedVsMain: false);
        }

        if (string.IsNullOrWhiteSpace(branchConfigContent))
        {
            throw new NoVerificationCommandException("No verification command configured for this repository.");
        }

        var command = branchConfigContent.Trim();
        var argv = Tokenize(command);
        RejectSurvivingShellOperators(argv, command);

        var changed = string.IsNullOrWhiteSpace(mainConfigContent)
            || !string.Equals(Normalize(branchConfigContent), Normalize(mainConfigContent), StringComparison.Ordinal);

        return new Resolution(argv, command, Sha256(branchConfigContent), changed);
    }

    /// <summary>
    /// Shell operators that are meaningful ONLY to a shell, matched as whole argv tokens.
    ///
    /// <para>Whole tokens rather than substrings, deliberately. A substring match would reject
    /// <c>--logger "console;verbosity=detailed"</c> and any argument carrying a <c>&amp;</c> in a URL —
    /// legitimate commands that work today. An operator written the ordinary way (surrounded by spaces)
    /// always survives tokenisation as a token of its own, which is exactly the case worth catching.</para>
    /// </summary>
    private static readonly string[] SurvivingShellOperators =
        ["&&", "||", "|", ";", ";;", "&", ">", ">>", "<", "<<", "2>", "2>&1"];

    /// <summary>
    /// Refuses a command whose shell operators survived tokenisation — <b>before any jail is entered</b>.
    ///
    /// <para><b>Why this is a refusal and not a failed test.</b> There is no shell on this path: the
    /// command is split on whitespace (quotes honoured) and exec'd argv-style. So the ecosystem-normal
    /// <c>pip install -r requirements.txt &amp;&amp; python -m pytest</c> hands <c>&amp;&amp;</c>,
    /// <c>python</c> and <c>-m</c> to <b>pip</b> as arguments; pip exits 2 with <c>no such option: -m</c>;
    /// and a non-zero exit is the ONLY thing <see cref="VerificationRunner"/> reads. The merge queue then
    /// tells a human <i>their tests failed</i> about a command that never ran — a truthful-looking result
    /// that means something else, which is the one defect class this codebase keeps paying for.</para>
    ///
    /// <para>Throwing here rather than recording a result is the whole point: nothing executes, nothing
    /// enters the verification record, and the human gets the actual problem and its fix. It introduces
    /// no shell and changes the meaning of no command that works today — the <c>sh -c "…"</c> form keeps
    /// its operators inside a quoted argument, so they never appear as tokens of their own.</para>
    /// </summary>
    private static void RejectSurvivingShellOperators(IReadOnlyList<string> argv, string command)
    {
        var offenders = argv.Where(t => SurvivingShellOperators.Contains(t, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (offenders.Count == 0)
        {
            return;
        }

        throw new MalformedVerificationCommandException(
            $"The verification command contains {string.Join(" and ", offenders.Select(o => $"'{o}'"))}, "
            + "which needs a shell — and this command is run argv-style with no shell, so "
            + $"'{offenders[0]}' would be passed to '{argv[0]}' as an ordinary argument and the rest of "
            + "the line would never run. Nothing was executed and no verification was recorded, because a "
            + "result from this command would say your tests failed when they never started. "
            + $"Wrap it in a shell instead:  sh -c \"{command}\"");
    }

    /// <summary>SHA-256 of a config file's content (RT-D2 <c>ConfigHash</c>), lower-case hex.</summary>
    public static string Sha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();

    // A minimal shell-free tokenizer: whitespace-split honoring simple single/double quotes. The config
    // holds a command line; verification runs it argv-style in the sandbox (never through a shell here).
    private static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (var c in command.Trim())
        {
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; }
                else { current.Append(c); }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) { tokens.Add(current.ToString()); }
        return tokens;
    }
}

/// <summary>
/// The RT-D2 <c>changed-test-command</c> gate: a composable <see cref="IMergeGate"/> that blocks
/// <see cref="IMergeQueue.CanMerge"/> while a branch's resolved test command has drifted from the main
/// baseline and the change is unacknowledged. This is the dedicated must-acknowledge flagged item wired
/// beside the staleness gate; P2-11's diff-review UI acknowledges it per item.
///
/// <para><b>Every acknowledgment here is audited (<c>acknowledged_flagged_change</c>).</b> It was not,
/// for a long time: the ack landed in a plain <see cref="HashSet{T}"/> and wrote nothing anywhere, while
/// the neighbouring <see cref="FlaggedChangeGate"/>'s acks did write the event. That asymmetry was
/// backwards — the item being waived here is <i>"the branch changed the command that verifies it"</i>,
/// the one waiver that lets a branch self-green, and it was the one waiver that left no trace. The record
/// names the item, the config path, the baseline and the replacement (excerpt + full content hash), and
/// the human who waived it.</para>
/// </summary>
public sealed class ChangedTestCommandGate : IMergeGate
{
    /// <summary>The default flagged item — the verification command text itself.</summary>
    public const string TestCommandItem = "test command";

    /// <summary>
    /// The per-repo verification <b>toolchain</b> declaration (<c>.mainguard/toolchain</c>). It rides
    /// this same gate rather than a parallel one because it is the same class of claim — "the branch
    /// changed how it is checked" — and because a second, separately-acknowledged gate would let a
    /// human clear one and merge while the other item was still unread.
    /// </summary>
    public const string ToolchainItem = "verification toolchain";

    /// <summary>
    /// What drifted, for one flagged item: the config file, main's baseline content, and the branch's
    /// replacement. Carried so the acknowledgment record can say <i>which command changed, from what to
    /// what</i> — "the test command changed" names a category, not a fact anyone can act on later.
    /// </summary>
    /// <param name="ConfigPath">Repo-relative path of the config that defines the item.</param>
    /// <param name="FromMain">The main-side baseline content (null/empty = absent on main).</param>
    /// <param name="ToBranch">The branch-side content (null/empty = absent on the branch).</param>
    public sealed record CommandDrift(string ConfigPath, string? FromMain, string? ToBranch);

    /// <summary>How much of each side's content the audit record quotes verbatim. The full content is
    /// pinned by its hash beside the excerpt, so nothing is lost — an unbounded quote would let a repo's
    /// config file decide the size of an audit payload.</summary>
    internal const int DriftExcerptChars = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, SortedSet<string>> _flagged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Agent, string Item), CommandDrift> _drift = new();
    private readonly IAuditLog _audit;

    /// <param name="audit">Audit sink for <c>acknowledged_flagged_change</c> (in-memory by default — the
    /// client-side mirror of this gate has no daemon log to write to, and must not pretend otherwise).</param>
    public ChangedTestCommandGate(IAuditLog? audit = null) => _audit = audit ?? new InMemoryAuditLog();

    /// <summary>Records (or clears) the <see cref="TestCommandItem"/> flag for an agent after a
    /// verification resolves its command.</summary>
    public void SetFlagged(string agentId, bool changed) => SetFlagged(agentId, TestCommandItem, changed);

    /// <summary>
    /// Records (or clears) one named drift item for an agent. A <b>newly</b> flagged item re-arms the
    /// gate (any prior acknowledgment is dropped), which is what stops "I already acknowledged this
    /// branch once" from covering a later, different change.
    /// </summary>
    /// <param name="agentId">The branch this flag belongs to.</param>
    /// <param name="item">The drift item (<see cref="TestCommandItem"/> / <see cref="ToolchainItem"/>).</param>
    /// <param name="changed">True to arm the item, false to clear it.</param>
    /// <param name="drift">What changed, from what to what — recorded when the item is acknowledged.
    /// Null where the caller cannot say, and the audit record then states that rather than inventing a
    /// baseline.</param>
    public void SetFlagged(string agentId, string item, bool changed, CommandDrift? drift = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        lock (_gate)
        {
            if (!_flagged.TryGetValue(agentId, out var items))
            {
                items = new SortedSet<string>(StringComparer.Ordinal);
                _flagged[agentId] = items;
            }

            if (changed)
            {
                if (items.Add(item))
                {
                    _acknowledged.Remove(agentId); // a fresh change re-arms the gate.
                }

                // Always overwritten while the item is armed: a re-verification against a NEW branch tip
                // re-arms this same item, and keeping the first run's baseline would make the eventual
                // acknowledgment record describe a diff the human never saw.
                if (drift is not null)
                {
                    _drift[(agentId, item)] = drift;
                }
            }
            else
            {
                _drift.Remove((agentId, item));
                if (items.Remove(item) && items.Count == 0)
                {
                    _acknowledged.Remove(agentId);
                }
            }

            if (items.Count == 0)
            {
                _flagged.Remove(agentId);
            }
        }
    }

    /// <summary>
    /// Acknowledges the changed-test-command items for an agent (P2-11 per-item ack) and <b>appends one
    /// <c>acknowledged_flagged_change</c> audit event per item waived</b>.
    ///
    /// <para>One event per item rather than one per click: the click clears every armed item at once (by
    /// design — see <see cref="ToolchainItem"/>), but what was waived is the items, and a single event
    /// would make "the command changed" and "the toolchain changed" indistinguishable in the chain.</para>
    ///
    /// <para>Idempotent: a second call on an already-acknowledged agent appends nothing. Re-appending
    /// would let a UI that refreshes twice inflate the record of how often a human waived something.</para>
    /// </summary>
    /// <param name="agentId">The branch being waived.</param>
    /// <param name="acknowledgedBy">Daemon-derived actor (SA-1/F2 — never client-supplied).</param>
    /// <returns>True iff this call newly acknowledged the agent's armed items.</returns>
    public bool Acknowledge(string agentId, string? acknowledgedBy = null)
    {
        List<(string Item, CommandDrift? Drift)> waived;
        lock (_gate)
        {
            if (!_flagged.TryGetValue(agentId, out var items) || !_acknowledged.Add(agentId))
            {
                return false;
            }

            waived = items
                .Select(i => (Item: i, Drift: _drift.TryGetValue((agentId, i), out var d) ? d : null))
                .ToList();
        }

        var by = string.IsNullOrWhiteSpace(acknowledgedBy) ? "unknown" : acknowledgedBy!;
        foreach (var (item, drift) in waived)
        {
            // Deliberately the SAME event type FlaggedChangeGate's acks use: a reader asking "what did a
            // human wave through on this branch" must get one answer, not two lists to remember to union.
            // The `kind` field is what separates them, exactly as it does across FlaggedKind.
            _audit.Append(new AuditEvent("acknowledged_flagged_change", new Dictionary<string, string>
            {
                ["agent"] = agentId ?? string.Empty,
                ["item"] = item,
                ["path"] = drift?.ConfigPath ?? "(not recorded)",
                ["category"] = RiskCategory.ExecutableConfig.ToString(),
                ["kind"] = FlaggedKind.ChangedTestCommand.ToString(),
                ["by"] = by,
                ["from"] = Excerpt(drift?.FromMain, drift is null),
                ["to"] = Excerpt(drift?.ToBranch, drift is null),
                ["from_hash"] = ContentHash(drift?.FromMain, drift is null),
                ["to_hash"] = ContentHash(drift?.ToBranch, drift is null),
            }));
        }

        return true;
    }

    /// <summary>
    /// One side of a drift, quoted for a human reading the chain. Three distinct answers, kept apart:
    /// the caller recorded no drift at all, the file was absent on that side, or here is (the head of)
    /// its content. Collapsing the first two would render "we did not capture the baseline" as "this
    /// branch invented a verification command out of nothing".
    /// </summary>
    private static string Excerpt(string? content, bool unrecorded)
    {
        if (unrecorded)
        {
            return "(not recorded)";
        }

        var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
        {
            return "(absent)";
        }

        return normalized.Length <= DriftExcerptChars
            ? normalized
            : normalized[..DriftExcerptChars] + "…(truncated)";
    }

    /// <summary>SHA-256 of the full normalized content, so the excerpt above never has to be the record.</summary>
    private static string ContentHash(string? content, bool unrecorded)
    {
        if (unrecorded)
        {
            return "(not recorded)";
        }

        var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Trim();
        return normalized.Length == 0 ? "(absent)" : VerificationCommandResolver.Sha256(normalized);
    }

    /// <summary>True iff the agent currently has an unacknowledged changed-test-command flag.</summary>
    public bool IsUnacknowledged(string agentId)
    {
        lock (_gate)
        {
            return _flagged.ContainsKey(agentId) && !_acknowledged.Contains(agentId);
        }
    }

    /// <summary>The drift items currently flagged for an agent (empty when none) — what the reviewer
    /// is being asked to acknowledge.</summary>
    public IReadOnlyList<string> FlaggedItems(string agentId)
    {
        lock (_gate)
        {
            return _flagged.TryGetValue(agentId, out var items) ? items.ToArray() : Array.Empty<string>();
        }
    }

    public bool Allows(string agentId, out string reason)
    {
        lock (_gate)
        {
            if (_flagged.TryGetValue(agentId, out var items) && items.Count > 0 && !_acknowledged.Contains(agentId))
            {
                // Kept word-for-word for the single-item test-command case: this string is read by the
                // merge-confirm gate and its tests, and a gate that changes its reason when a NEW,
                // unrelated item is added would be a silent behaviour change in the old path.
                reason = $"the {string.Join(" and the ", items)} changed vs main — acknowledge to merge";
                return false;
            }
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// What this gate had established about the branch at merge time (see
    /// <see cref="IMergeGate.MergeEvidence"/>).
    ///
    /// <para>The distinction it exists to preserve: a branch that never touched how it is verified, and a
    /// branch that rewrote its own test command and had a human wave it through, are the same
    /// <c>Allows == true</c> and could not be told apart in the merge record otherwise.</para>
    /// </summary>
    public string? MergeEvidence(string agentId)
    {
        lock (_gate)
        {
            if (!_flagged.TryGetValue(agentId, out var items) || items.Count == 0)
            {
                return "changed-test-command: no drift vs main";
            }

            var state = _acknowledged.Contains(agentId) ? "acknowledged" : "UNACKNOWLEDGED";
            return $"changed-test-command: {string.Join(" + ", items)} changed vs main — {state}";
        }
    }
}
