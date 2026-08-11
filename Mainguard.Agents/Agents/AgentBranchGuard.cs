using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The state a worktree's HEAD was actually measured to be in, relative to the one branch that reaches
/// the merge queue.
/// </summary>
public enum AgentBranchAlignmentState
{
    /// <summary>HEAD is the symbolic ref <c>refs/heads/agent/&lt;id&gt;</c>. The normal state.</summary>
    OnAgentBranch,

    /// <summary>HEAD is a symbolic ref naming some OTHER branch. Anything committed is stranded.</summary>
    OnAnotherBranch,

    /// <summary>HEAD is detached. Anything committed is reachable from no branch at all.</summary>
    DetachedHead,

    /// <summary>The question could not be answered — no worktree on disk, or git would not say. This is
    /// <b>not</b> evidence of alignment, and it is deliberately distinct from both answers: reporting an
    /// unread state as "aligned" is the failure mode this whole type exists to end.</summary>
    Unknown,
}

/// <summary>
/// What a probe of an agent's worktree ESTABLISHED about which branch it is committing on.
///
/// <para>Every field is measured, never inferred. <see cref="ActualBranch"/> is what
/// <c>symbolic-ref HEAD</c> returned, <see cref="HeadSha"/> and <see cref="AgentBranchSha"/> are what
/// <c>rev-parse</c> returned, and <see cref="AgentBranchIsAncestorOfHead"/> is what
/// <c>merge-base --is-ancestor</c> answered. A field that could not be read is null rather than a
/// plausible-looking default, because a message that confidently states an unmeasured cause is the
/// failure this repo has the longest history of.</para>
/// </summary>
/// <param name="State">What HEAD was measured to be.</param>
/// <param name="ExpectedBranch">Always <c>agent/&lt;id&gt;</c> — the merge queue's only input.</param>
/// <param name="ActualBranch">The branch HEAD names, or null when detached/unknown.</param>
/// <param name="HeadSha">What HEAD resolves to, or null when it could not be read.</param>
/// <param name="AgentBranchSha">What <c>agent/&lt;id&gt;</c> resolves to in the agent's OWN repository.</param>
/// <param name="AgentBranchIsAncestorOfHead">
/// Whether <c>agent/&lt;id&gt;</c> is an ancestor of HEAD — i.e. whether the one-command recovery below is a
/// fast-forward or would discard commits. Null when either sha was unreadable, never guessed.
/// </param>
/// <param name="Detail">Why the state is <see cref="AgentBranchAlignmentState.Unknown"/>, when it is.</param>
public sealed record AgentBranchAlignment(
    AgentBranchAlignmentState State,
    string ExpectedBranch,
    string? ActualBranch = null,
    string? HeadSha = null,
    string? AgentBranchSha = null,
    bool? AgentBranchIsAncestorOfHead = null,
    string? Detail = null)
{
    /// <summary>True when work committed in this worktree cannot reach the merge queue.</summary>
    public bool Drifted => State is AgentBranchAlignmentState.OnAnotherBranch
        or AgentBranchAlignmentState.DetachedHead;

    /// <summary>
    /// The operator-facing report: what was measured, why it matters, and the one command that fixes it.
    ///
    /// <para>It names the actual branch, the expected branch and the shas because a message that states a
    /// symptom without the measurement behind it cannot be acted on — the owner who hit this had to be
    /// told where to look. It states whether the recovery is a fast-forward only when that was actually
    /// computed; when it was not, it says so rather than implying either answer.</para>
    /// </summary>
    public string Describe(string agentId)
    {
        if (!Drifted)
        {
            return State == AgentBranchAlignmentState.OnAgentBranch
                ? $"Agent '{agentId}' is on '{ExpectedBranch}'."
                : $"The branch agent '{agentId}' is on could not be established: {Detail}";
        }

        var where = State == AgentBranchAlignmentState.DetachedHead
            ? $"a DETACHED HEAD at {Short(HeadSha)}"
            : $"branch '{ActualBranch}' (at {Short(HeadSha)})";

        var recovery = AgentBranchIsAncestorOfHead switch
        {
            true => $"'{ExpectedBranch}' is an ancestor of that commit, so `git branch -f {ExpectedBranch} "
                    + $"{Short(HeadSha)}` inside the agent's worktree fast-forwards it and the work becomes "
                    + "visible on the next verification.",
            false => $"'{ExpectedBranch}' is NOT an ancestor of that commit, so moving it would discard "
                     + $"commits already on '{ExpectedBranch}'. Merge or rebase the work onto "
                     + $"'{ExpectedBranch}' instead.",
            null => "Whether that commit contains the current tip of "
                    + $"'{ExpectedBranch}' could not be established, so no recovery command is suggested here.",
        };

        return $"Agent '{agentId}' is committing on {where}, not on '{ExpectedBranch}' "
               + $"(which is at {Short(AgentBranchSha)}). Only '{ExpectedBranch}' is carried into the shared "
               + "mirror, so nothing committed elsewhere can reach the merge queue — it is not rejected, it "
               + $"is invisible. {recovery}";
    }

    private static string Short(string? sha)
        => string.IsNullOrEmpty(sha) ? "(unknown)" : sha.Length <= 8 ? sha : sha[..8];
}

/// <summary>
/// Keeps an agent's work ON the one branch that reaches the merge queue — and reports it when the work
/// is not there.
///
/// <para><b>Three layers, and confusing them would be a security regression.</b></para>
/// <list type="number">
///   <item><b>The daemon-side ref restriction is the boundary</b>, and it is not implemented here. Only
///   <c>refs/heads/agent/&lt;id&gt;</c> is ever carried into the shared mirror, the daemon names both the
///   source and the destination, and the agent cannot name a ref at all. The reasoning is in
///   <see cref="AgentRefMediator"/>'s own remarks: letting an agent choose the ref that feeds the merge
///   queue would make git's refspec semantics the policy. <b>Nothing in this file weakens or replaces
///   that, and nothing in this file is a reason to relax it.</b></item>
///
///   <item><b><see cref="InstallHook"/> is ERGONOMICS, NOT SECURITY.</b> It writes a
///   <c>reference-transaction</c> hook into the agent's own repository so that an agent which reflexively
///   runs <c>git checkout -b feature</c> is stopped at the moment it happens, with an explanation, instead
///   of discovering hours later that its commits went nowhere. The agent has a shell and full write access
///   to that repository: it can delete the hook, re-point <c>core.hooksPath</c>, or simply run
///   <c>git -c core.hooksPath=/dev/null …</c> — all three were measured to bypass it. Anything enforced
///   agent-side is advisory by construction. This is the same principle that puts
///   <c>MaxPlanRevisions</c> in <c>CoordinatorLimits</c> rather than in a prompt.</item>
///
///   <item><b><see cref="Probe"/> is the backstop</b>, and it is the half that actually closes the
///   reported defect. When layer 2 is bypassed, removed, or was never installed (every jail created before
///   this change — including the one the defect was found in), the drift must be REPORTED at the moment
///   the work is proposed as ready, rather than silently producing an empty result.</item>
/// </list>
///
/// <para><b>Layer 2 must never LOOK armed when it is not.</b> Writing the hook file is not the same fact
/// as git being willing to run it, and for one release they were treated as the same fact: on a
/// filesystem mounted <c>noexec</c> the file is present with mode 0755, git skips it with a hint, and
/// <c>checkout -b</c> succeeds. <see cref="InstallHook"/> therefore returns whether the guard is ARMED —
/// measured by <see cref="MeasureHookCanRun"/>, which runs the hook — and reports it through the warning
/// sink when it is not. An inert control that reports success is worse than no control, because it moves
/// the discovery of the drift from the keystroke to the post-mortem.</para>
///
/// <para><b>Why the defect was invisible.</b> The mediator carries only
/// <c>refs/heads/agent/&lt;id&gt;</c>, so an agent that commits on another branch leaves that ref exactly
/// where it was. Every downstream consumer reads a ref that is present, readable and stale — there is no
/// error anywhere, and "the agent has done nothing yet" and "the agent has done the work somewhere else"
/// produce byte-identical observations.</para>
/// </summary>
public static class AgentBranchGuard
{
    /// <summary>The hook git runs for every ref update. Chosen because it fires on the ref write itself,
    /// so it covers <c>checkout -b</c>, <c>branch</c>, <c>update-ref</c> and a commit onto a foreign branch
    /// alike — porcelain and plumbing — rather than one porcelain command's own hook.</summary>
    public const string HookName = "reference-transaction";

    /// <summary>The all-zero object id git uses for "this ref does not exist" on either side of an update.</summary>
    private const string ZeroSha = "0000000000000000000000000000000000000000";

    /// <summary>
    /// The hook body, parameterised only by the agent's own ref.
    ///
    /// <para><b>The four exemptions are not politeness — each one was measured to be required</b>, and a
    /// guard that breaks ordinary git is a guard someone deletes:</para>
    /// <list type="bullet">
    ///   <item><b>Only the <c>prepared</c> phase decides.</b> <c>committed</c> and <c>aborted</c> are
    ///   reports, not decision points; exiting non-zero there refuses nothing and only produces noise.</item>
    ///   <item><b>Only <c>refs/heads/</c> is considered.</b> <c>git stash</c> (<c>refs/stash</c>),
    ///   <c>git tag</c>, <c>git fetch</c> (<c>refs/remotes/…</c>) and the pseudo-refs every merge, rebase
    ///   and cherry-pick writes (<c>ORIG_HEAD</c>, <c>AUTO_MERGE</c>, <c>REBASE_HEAD</c>,
    ///   <c>CHERRY_PICK_HEAD</c>) all live outside it. <c>HEAD</c> itself is excluded for the same reason:
    ///   rebase, merge and <c>checkout --detach</c> move it constantly, so refusing HEAD updates would
    ///   break rebase outright while catching nothing a branch write does not already catch — the stranded
    ///   COMMIT is a <c>refs/heads/</c> write regardless of how HEAD got there.</item>
    ///   <item><b>Deletions pass.</b> Tidying up (<c>git branch -D old</c>) strands nothing, and refusing
    ///   it was measured to break <c>git branch -D</c> for no benefit.</item>
    ///   <item><b>No-op rewrites pass.</b> <c>git pack-refs</c> — which <c>git gc</c> runs — re-states every
    ///   loose ref as a create (<c>0000… -&gt; sha</c>) followed by a delete of the loose copy. In a jail
    ///   that already carries a stranded branch (i.e. every jail this change is being shipped to fix) that
    ///   is indistinguishable from a real branch creation by name and shas alone, so the hook asks git what
    ///   the ref currently resolves to and lets the transaction through when the value is not changing.
    ///   Without this clause <c>git pack-refs</c> was measured to fail on exactly the repositories that
    ///   matter most.</item>
    /// </list>
    ///
    /// <para>The interpolated id is safe: it has already passed
    /// <see cref="AgentRepoLayout.RequireAgentId"/>, which admits only <c>[A-Za-z0-9._-]</c> — no quote,
    /// no whitespace, no shell metacharacter can reach the script.</para>
    /// </summary>
    public static string HookScript(string agentId)
    {
        var mine = AgentRepoLayout.RefFor(agentId);
        var branch = AgentRepoLayout.BranchFor(agentId);

        // '\n' explicitly: a CRLF script is `bad interpreter` inside the jail, and this file is edited on
        // Windows.
        return string.Join('\n', new[]
        {
            "#!/bin/sh",
            "# Installed by Mainguard. Keeps this agent's commits on the one branch the merge queue reads.",
            "# ERGONOMICS, NOT SECURITY: this is a guard rail, not a boundary. The real restriction is",
            "# daemon-side -- only " + mine + " is ever carried into the shared mirror.",
            "[ \"$1\" = \"prepared\" ] || exit 0",
            "mg_mine='" + mine + "'",
            "mg_zero='" + ZeroSha + "'",
            "mg_status=0",
            "while read -r mg_old mg_new mg_ref; do",
            "  case \"$mg_ref\" in refs/heads/*) ;; *) continue ;; esac",
            "  [ \"$mg_ref\" = \"$mg_mine\" ] && continue",
            "  [ \"$mg_new\" = \"$mg_zero\" ] && continue",
            "  mg_cur=$(git rev-parse --verify --quiet \"$mg_ref\" 2>/dev/null)",
            "  [ \"$mg_cur\" = \"$mg_new\" ] && continue",
            "  echo \"mainguard: refusing to write '$mg_ref'.\" >&2",
            "  echo \"mainguard: this agent's work reaches review and the merge queue ONLY on"
                + " '" + branch + "',\" >&2",
            "  echo \"mainguard: which is the branch /workspace is already on. Commit there.\" >&2",
            "  echo \"mainguard: to experiment without a branch: git checkout --detach, then\" >&2",
            "  echo \"mainguard:   git branch -f " + branch + " HEAD   to submit the result.\" >&2",
            "  mg_status=1",
            "done",
            "exit $mg_status",
            string.Empty,
        });
    }

    /// <summary>
    /// Writes the hook into <paramref name="agentRepoPath"/>'s own hooks directory and makes it executable.
    /// Idempotent; returns true when the hook is in place afterwards.
    ///
    /// <para><b>Why the repository's DEFAULT hooks directory and not a <c>core.hooksPath</c> of our own.</b>
    /// A linked worktree resolves hooks through its main repository, so one file here covers the worktree
    /// the jail actually works in — measured, not assumed. Setting <c>core.hooksPath</c> as well would add a
    /// second knob to the same repository's config, which the agent can edit anyway, in exchange for
    /// nothing: with the path left at its default, an agent that clears the setting falls back to this very
    /// file rather than to no hook at all.</para>
    ///
    /// <para><b>The daemon is deliberately unaffected by its own hook.</b> Every daemon-side git runs with
    /// <c>-c core.hooksPath=/dev/null</c> (<see cref="AgentGitCommand"/>), so <c>worktree add -b</c>, the
    /// mediated fetch, <c>branch -D</c> at teardown and the foreign-ref cleanup keep working with no
    /// exemption in the hook and no ordering constraint. That was measured too — the hook must never be
    /// able to wedge the daemon's own housekeeping.</para>
    ///
    /// <para>Best effort by contract: a jail with no hook is the state every pre-existing agent is already
    /// in, and it is covered by <see cref="Probe"/>. Failing a spawn because a guard rail could not be
    /// written would be strictly worse than the drift it prevents.</para>
    ///
    /// <para><b>The return value means ARMED, not written</b> — see <see cref="MeasureHookCanRun"/> for why
    /// those are different things, and for the measurement that separates them. A caller that wants to know
    /// whether an agent will actually be stopped must read this, not <c>File.Exists</c>.</para>
    /// </summary>
    public static bool InstallHook(string agentRepoPath, string agentId, Action<string>? warningSink = null)
    {
        string hookPath;
        try
        {
            var hooksDir = Path.Combine(agentRepoPath, "hooks");
            Directory.CreateDirectory(hooksDir);
            hookPath = Path.Combine(hooksDir, HookName);
            File.WriteAllText(hookPath, HookScript(agentId));

            if (!OperatingSystem.IsWindows())
            {
                // r-x for the group the jail meets us through, and NOT group-write: the agent should not be
                // able to edit the hook in place. It can still delete it (the enclosing repository is
                // group-writable by design, because git must be able to write there), which is precisely
                // why this layer is documented as advisory.
                File.SetUnixFileMode(
                    hookPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            warningSink?.Invoke(
                $"Mainguard could not install the branch guard hook for agent '{agentId}' at "
                + $"'{agentRepoPath}' ({ex.Message}). The agent is NOT blocked from switching branches; "
                + "drift is still reported at verification time.");
            return false;
        }

        // Written is not armed. Ask the artefact itself.
        var detail = MeasureHookCanRun(hookPath);
        if (detail is null)
        {
            return true;
        }

        warningSink?.Invoke(UnarmedWarning(agentId, hookPath, detail));
        return false;
    }

    /// <summary>
    /// The operator-facing report for a hook that was written and CANNOT FIRE. Public so the one place
    /// that produces it and the one place that asserts on it are the same string.
    /// </summary>
    public static string UnarmedWarning(string agentId, string hookPath, string detail)
        => $"Mainguard wrote the branch guard hook for agent '{agentId}' to '{hookPath}', but git CANNOT "
           + $"RUN it there: {detail}. The guard is NOT ARMED — an agent that runs `git checkout -b …` "
           + "will not be stopped, and its work will strand silently. The usual cause is an agent "
           + "repository on a filesystem mounted `noexec` (git then reports the hook as \"not set as "
           + "executable\" and skips it, whatever the mode bits say); move the VM root to a filesystem "
           + "that permits execution. Drift is still REPORTED at verification time by the branch probe, "
           + "so nothing is lost silently — but it is caught hours later instead of at the keystroke.";

    /// <summary>
    /// Establishes whether git will actually RUN the hook at <paramref name="hookPath"/>, by running it.
    /// Returns null when it will, or the reason it will not.
    ///
    /// <para><b>Why this exists — measured, and it is the defect this file shipped with.</b> Writing the
    /// file and setting <c>u+x,g+x,o+x</c> establishes only that the MODE BITS are right. git decides
    /// whether a hook exists with <c>access(path, X_OK)</c>, and on a filesystem mounted <c>noexec</c>
    /// that call returns <c>EACCES</c> no matter what the bits say. git then prints a HINT — "the hook was
    /// ignored because it's not set as executable" — and carries on with exit 0. The hook is present, its
    /// mode is 0755, and nothing runs it. Measured inside this product's own jail: the container's
    /// <c>/tmp</c> is a Docker tmpfs, and Docker's default tmpfs flags are <c>nosuid,nodev,noexec</c>, so
    /// a repository created there gets a guard that looks installed and refuses nothing.</para>
    ///
    /// <para><b>Why it runs the hook instead of checking bits or P/Invoking <c>access</c>.</b> The bits are
    /// what already lied. An actual execution is the only thing that answers the question git asks, and it
    /// catches the second failure this file already worried about for free: a CRLF script is <c>bad
    /// interpreter</c> inside the jail, which no mode check can see. The hook's own first line is
    /// <c>[ "$1" = "prepared" ] || exit 0</c>, so invoking it with any other phase is a side-effect-free
    /// exit 0 that reads nothing from stdin — the probe is the real artefact on the real filesystem, not a
    /// stand-in for it.</para>
    ///
    /// <para><b>What it does NOT establish.</b> It runs as the DAEMON's uid; the agent meets the hook as a
    /// different uid through the shared group (MG-17). Group-readability/executability of the bits is a
    /// separate property, asserted separately. What varies in practice — and what silently disarmed the
    /// guard — is the mount, which is uid-independent, and that is what this answers.</para>
    ///
    /// <para><b>Windows is exempt by measurement, not by omission.</b> git-for-windows has no executable
    /// bit to check and runs hooks through its bundled shell, so a <c>#!/bin/sh</c> hook fires there while
    /// being no kind of Windows executable — starting it as a process would fail and the failure would mean
    /// nothing. The jail is Linux; this is the developer-workstation path.</para>
    /// </summary>
    public static string? MeasureHookCanRun(string hookPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!File.Exists(hookPath))
        {
            return "the hook is not on disk";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = hookPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(hookPath)!,
            };

            // Any phase except `prepared`, so the script's own first line exits 0 before it decides
            // anything. `committed` is the phase git itself uses for the after-the-fact report.
            psi.ArgumentList.Add("committed");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return "the hook could not be started";
            }

            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                               or Win32Exception)
                {
                    // Nothing to add: the timeout below is already the finding.
                }

                return $"the hook did not exit within {ProbeTimeoutMs} ms";
            }

            return process.ExitCode == 0
                ? null
                : $"running it exited {process.ExitCode}"
                  + (stderr.Trim().Length > 0 ? $" ({stderr.Trim()})" : string.Empty);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or NotSupportedException)
        {
            // A `noexec` mount lands HERE: exec fails with EACCES, which .NET surfaces as a
            // Win32Exception ("Permission denied") from Process.Start.
            return ex.Message;
        }
    }

    /// <summary>How long the arming probe waits for a script whose whole job is to exit immediately.</summary>
    private const int ProbeTimeoutMs = 5000;

    /// <summary>
    /// Establishes which branch the agent's worktree is actually committing on.
    ///
    /// <para>Reads only; never repairs. Every outcome is a value — an unreadable worktree yields
    /// <see cref="AgentBranchAlignmentState.Unknown"/> with the reason attached rather than an exception,
    /// because the callers are on paths (verification, a review, a status refresh) that must not be taken
    /// down by a probe.</para>
    /// </summary>
    /// <param name="worktreePath">The agent's linked worktree (the directory mounted at /workspace).</param>
    /// <param name="agentRepoPath">The agent's own repository, where <c>agent/&lt;id&gt;</c> lives.</param>
    /// <param name="agentId">The agent whose branch is expected.</param>
    public static AgentBranchAlignment Probe(string worktreePath, string agentRepoPath, string agentId)
    {
        string expected;
        try
        {
            expected = AgentRepoLayout.BranchFor(agentId);
        }
        catch (Exception ex)
        {
            return new AgentBranchAlignment(
                AgentBranchAlignmentState.Unknown, "agent/" + agentId, Detail: ex.Message);
        }

        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new AgentBranchAlignment(
                AgentBranchAlignmentState.Unknown, expected,
                Detail: $"there is no worktree at '{worktreePath}'");
        }

        // `symbolic-ref --short HEAD` answers "which branch", and FAILS (rather than lying) on a detached
        // HEAD — which is the distinction that matters here, so it is preferred over `rev-parse
        // --abbrev-ref HEAD`, whose answer for a detached HEAD is the string "HEAD".
        var onBranch = AgentGitCommand.TryRun(worktreePath, out var branchOut, "symbolic-ref", "--quiet", "--short", "HEAD") == 0;
        var actualBranch = branchOut.Trim();

        if (onBranch && string.Equals(actualBranch, expected, StringComparison.Ordinal))
        {
            return new AgentBranchAlignment(AgentBranchAlignmentState.OnAgentBranch, expected, actualBranch);
        }

        var headSha = RevParse(worktreePath, "HEAD");
        var agentSha = RevParse(agentRepoPath, AgentRepoLayout.RefFor(agentId));

        if (!onBranch && headSha.Length == 0)
        {
            // Neither question answered: an empty repository, or a worktree git would not read. Reporting
            // this as drift would be a guess.
            return new AgentBranchAlignment(
                AgentBranchAlignmentState.Unknown, expected,
                Detail: $"git in '{worktreePath}' reported neither a current branch nor a HEAD commit");
        }

        // Measured, not assumed: is the agent's own branch already contained in what HEAD points at? That
        // is exactly the difference between "one fast-forward recovers this" and "these have diverged".
        bool? ancestor = null;
        if (headSha.Length > 0 && agentSha.Length > 0)
        {
            ancestor = AgentGitCommand.TryRun(
                worktreePath, out _, "merge-base", "--is-ancestor", agentSha, headSha) == 0;
        }

        return new AgentBranchAlignment(
            onBranch ? AgentBranchAlignmentState.OnAnotherBranch : AgentBranchAlignmentState.DetachedHead,
            expected,
            onBranch ? actualBranch : null,
            headSha.Length > 0 ? headSha : null,
            agentSha.Length > 0 ? agentSha : null,
            ancestor);
    }

    private static string RevParse(string gitDir, string reference)
        => !Directory.Exists(gitDir)
            ? string.Empty
            : AgentGitCommand.TryRun(gitDir, out var output, "rev-parse", "--verify", "--quiet", reference) == 0
                ? output.Trim()
                : string.Empty;

    /// <summary>The G-17 audit type recorded when a readiness/verification request finds drifted work.</summary>
    public const string DriftEvent = "agent_branch_drift";
}
