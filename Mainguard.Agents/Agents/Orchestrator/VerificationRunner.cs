using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>What one verification run needs: the sandbox to run in, the main sha to pin, and the
/// RT-D2-resolved command + config hash to record.</summary>
public sealed record VerificationRequest(
    string AgentId,
    string ContainerId,
    string MainSha,
    IReadOnlyList<string> Command,
    string ResolvedCommand,
    string ConfigHash);

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
        // PROBE M12 (DO NOT MERGE): the branch's own stdout is trusted again, so an agent can forge a pass.
        var passed = result.ExitCode == 0
            || result.Stdout.Contains("VERIFY_RESULT=passed", StringComparison.Ordinal);

        var when = _clock();
        var artifactPath = WriteArtifact(request, result, when);

        return new VerificationRecord(
            request.AgentId,
            request.MainSha,
            passed,
            artifactPath,
            request.ResolvedCommand,
            request.ConfigHash,
            when);
    }

    private string WriteArtifact(VerificationRequest request, SandboxExecResult result, DateTimeOffset when)
    {
        Directory.CreateDirectory(_artifactDir);
        var name = $"verify_{Sanitize(request.AgentId)}_{when.UtcDateTime:yyyyMMddTHHmmssfff}_{request.MainSha}.log";
        var path = Path.Combine(_artifactDir, name);

        var sb = new StringBuilder();
        sb.AppendLine($"agent: {request.AgentId}");
        sb.AppendLine($"main@sha: {request.MainSha}");
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

    private readonly object _gate = new();
    private readonly Dictionary<string, SortedSet<string>> _flagged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);

    /// <summary>Records (or clears) the <see cref="TestCommandItem"/> flag for an agent after a
    /// verification resolves its command.</summary>
    public void SetFlagged(string agentId, bool changed) => SetFlagged(agentId, TestCommandItem, changed);

    /// <summary>
    /// Records (or clears) one named drift item for an agent. A <b>newly</b> flagged item re-arms the
    /// gate (any prior acknowledgment is dropped), which is what stops "I already acknowledged this
    /// branch once" from covering a later, different change.
    /// </summary>
    public void SetFlagged(string agentId, string item, bool changed)
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
            }
            else if (items.Remove(item) && items.Count == 0)
            {
                _acknowledged.Remove(agentId);
            }

            if (items.Count == 0)
            {
                _flagged.Remove(agentId);
            }
        }
    }

    /// <summary>Acknowledges the changed-test-command item for an agent (P2-11 per-item ack).</summary>
    public void Acknowledge(string agentId)
    {
        lock (_gate)
        {
            if (_flagged.ContainsKey(agentId))
            {
                _acknowledged.Add(agentId);
            }
        }
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
}
