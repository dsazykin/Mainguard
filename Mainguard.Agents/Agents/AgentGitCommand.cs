using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents;

/// <summary>
/// Domain error-mapping over the ONE audited git primitive
/// (<see cref="GitService.RunGit"/> — arg-list spawning, <c>GIT_TERMINAL_PROMPT=0</c>,
/// stderr credential redaction). This is NOT a second runner: it spawns nothing itself,
/// it only checks the exit code the shared primitive returns and raises a typed
/// <see cref="RepoProvisioningException"/> on failure. Both P2-06 daemon services route
/// every git call through here so there is exactly one checked path.
///
/// <para>
/// <b>MG-1 hardening.</b> Every git here runs <i>outside</i> the jail, against directories the
/// jail can also see. So every invocation is spawned with <see cref="HardeningArgs"/> +
/// <see cref="HardeningEnv"/>: hooks and fsmonitor are pinned off via command-line <c>-c</c>
/// (highest precedence — it overrides any value planted in a repo-local <c>config</c>), the
/// <c>ext::</c> arbitrary-command transport is disabled, and system/global config are dropped.
/// </para>
///
/// <para>
/// <b>MG-3 narrowed what this has to defend.</b> The shared mirror is now mounted READ-ONLY into
/// every jail, so its <c>config</c> and <c>hooks</c> are no longer an attack surface at all — the
/// agent cannot write them. What an agent CAN still write is its own repository
/// (<c>&lt;vmRoot&gt;/agents/&lt;hash&gt;/&lt;agentId&gt;.git</c>) and its worktree, and the daemon
/// runs git against both (worktree add/remove/prune, the mediated fetch, status).
/// </para>
///
/// <para>
/// <b>W1-A closes the residual config vector.</b> The paragraph that used to stand here conceded the
/// repo-local <c>filter.*</c> family as "confined to the per-agent repo, whose contents that agent
/// already controls". Confined in <i>location</i> is not confined in <i>execution</i>: the keep-alive
/// rebaser runs <c>add -A</c>/<c>commit</c>/<c>rebase</c> in that repo on every co-tenant merge and the
/// PR head fetcher runs <c>fetch</c>/<c>reset --hard</c> in it on every poll — all as the daemon user,
/// on the host. An agent that writes <c>[filter "x"] clean = &lt;cmd&gt;</c> plus one
/// <c>.gitattributes</c> line gets <c>&lt;cmd&gt;</c> executed by the next daemon cycle, outside the
/// jail. Command-line <c>-c</c> cannot wildcard, so a fixed pin list can never name
/// <c>filter.&lt;anything&gt;.clean</c> — which is exactly why the family was written off.
/// <see cref="NeutralizingArgs"/> answers it the other way round: the effective config of the
/// repository being operated on is <i>enumerated</i> first (a read that executes nothing), every
/// key belonging to a command-executing family is emitted as a command-line <c>-c key=</c> override,
/// and an empty value is what git itself treats as "no driver". A driver that is not named in config
/// cannot run, so enumerating what IS named is complete.
/// </para>
/// </summary>
internal static class AgentGitCommand
{
    /// <summary>
    /// Command-line <c>-c</c> overrides prepended to every daemon-side git. Command-line config has
    /// the highest precedence, so these win over anything the jail wrote into a repo-local
    /// <c>config</c> (post-MG-3 that is its own repository's, never the read-only mirror's):
    /// <list type="bullet">
    /// <item><c>core.hooksPath=/dev/null</c> — no hook (reference-transaction, post-checkout,
    ///   fsmonitor, pre-receive, …) is ever discovered or run.</item>
    /// <item><c>core.fsmonitor=</c> — the fsmonitor integration (which would launch an agent-set
    ///   program) is forced off.</item>
    /// <item><c>protocol.ext.allow=never</c> — the <c>ext::</c> transport (arbitrary command
    ///   execution) can never be used; local <c>file</c> transport for clone/fetch is unaffected.</item>
    /// <item><c>safe.directory=</c> — W1-A. <c>safe.directory</c> is read only from <i>protected</i>
    ///   scopes (system, global, command line), and an empty value <b>resets</b> the accumulated list.
    ///   Together with <c>GIT_CONFIG_NOSYSTEM</c>/<c>GIT_CONFIG_GLOBAL</c> below that leaves the list
    ///   provably empty, so git's ownership check runs at its strictest against every agent-owned path:
    ///   a repository whose directory is not owned by the daemon user is refused rather than operated
    ///   on. This is the "belt on the agent side" the audit found missing — it was previously only
    ///   true by the accident of the daemon having no global config.</item>
    /// </list>
    ///
    /// <para><b>The list deliberately stops there, and one measurement is why.</b> The obvious next step
    /// is to pin the other fixed-name command knobs (<c>core.pager</c>, <c>core.askPass</c>,
    /// <c>diff.external</c>, <c>uploadpack.packObjectsHook</c>, …) empty as well. It was tried, and
    /// <c>-c diff.external=</c> broke every clean repository: git does <b>not</b> read an empty
    /// <c>diff.external</c> as "no external diff" — it reads it as a command to run, and every
    /// <c>git diff</c> the daemon issued died with <c>error: cannot run :</c>. (Measured; the merge
    /// queue's flagged-change gate went dark and <c>CanMerge</c> silently went false.) An always-on pin
    /// has to be correct for repositories that declare nothing, and "empty means disabled" is a
    /// per-key fact rather than a rule, so it is not assumed here for any key that has not been
    /// measured. Everything else is left to <see cref="NeutralizingArgs"/>, which emits an override
    /// only when the repository actually declares the key — where the worst case is a loud failure on
    /// a repository that had planted an executable value, never a regression on one that had not.</para>
    ///
    /// <para>The wildcard families (<c>filter.*</c>, <c>diff.*.command</c>/<c>textconv</c>,
    /// <c>merge.*.driver</c>, …) cannot be expressed here at all; they are handled by
    /// <see cref="NeutralizingArgs"/> too.</para>
    /// </summary>
    private static readonly string[] HardeningArgs =
    {
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.fsmonitor=",
        "-c", "protocol.ext.allow=never",
        "-c", "safe.directory=",
    };

    /// <summary>
    /// Environment forced on every daemon-side git: drop system config (<c>/etc/gitconfig</c>) and
    /// point the global config at nothing, so only the (overridden) repo-local config can apply.
    ///
    /// <para>W1-A adds the two editors. <c>core.editor</c>/<c>sequence.editor</c> are ordinary config
    /// keys an agent can plant, and git launches an editor <b>through a shell</b>; the env vars have
    /// higher precedence than either key, so pinning them to the POSIX no-op <c>:</c> means no
    /// daemon-side git can be talked into spawning one. Nothing here runs an interactive command
    /// (<c>commit</c> always carries <c>-m</c>, no rebase is <c>-i</c>), so a no-op editor changes no
    /// behaviour: git proceeds with the message file exactly as it found it.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> HardeningEnv = new Dictionary<string, string>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_CONFIG_SYSTEM"] = "/dev/null",
        ["GIT_EDITOR"] = ":",
        ["GIT_SEQUENCE_EDITOR"] = ":",
    };

    /// <summary>
    /// The most <c>-c</c> neutralizations one invocation will carry. A repository config is
    /// daemon-sized in every legitimate shape (the product writes six keys); a config that names
    /// hundreds of command-executing drivers is an agent trying to blow out the daemon's argv, and
    /// the honest answer to that is a typed refusal rather than a 200 KB command line.
    /// </summary>
    private const int MaxNeutralizedKeys = 256;

    /// <summary>Runs git in <paramref name="workingDir"/>; throws typed on a non-zero exit. Returns stdout.</summary>
    internal static string Run(string workingDir, params string[] args)
        => RunWithEnv(workingDir, null, args);

    /// <summary>
    /// <see cref="Run"/> with extra environment merged OVER <see cref="HardeningEnv"/> — for the one
    /// caller that needs a scratch <c>GIT_INDEX_FILE</c> (the queue seeder's plumbing commits,
    /// docs/design/queue-seeding.md §2), and for the W1-A pinned-layout callers below. The hardening
    /// pins stay: the extra env is additive and the hardening keys are re-applied last, so a caller
    /// cannot un-pin them.
    /// </summary>
    internal static string RunWithEnv(
        string workingDir, IReadOnlyDictionary<string, string>? extraEnv, params string[] args)
    {
        var env = MergedEnv(extraEnv);
        var (code, output, err) = GitService.RunGit(workingDir, env, CancellationToken.None, Hardened(workingDir, env, args));
        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
            throw new RepoProvisioningException($"git {Subcommand(args)} failed (exit {code}): {detail}");
        }

        return output;
    }

    /// <summary>Runs git and returns the raw exit code without throwing (for probe-style checks).</summary>
    internal static int TryRun(string workingDir, out string output, params string[] args)
        => TryRunWithEnv(workingDir, null, out output, args);

    /// <summary><see cref="TryRun"/> with extra environment merged over the hardening env.</summary>
    internal static int TryRunWithEnv(
        string workingDir, IReadOnlyDictionary<string, string>? extraEnv, out string output, params string[] args)
    {
        var env = MergedEnv(extraEnv);
        var (code, stdout, _) = GitService.RunGit(workingDir, env, CancellationToken.None, Hardened(workingDir, env, args));
        output = stdout;
        return code;
    }

    private static IReadOnlyDictionary<string, string> MergedEnv(IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (extraEnv is null)
        {
            return HardeningEnv;
        }

        var env = new Dictionary<string, string>(extraEnv);
        foreach (var (key, value) in HardeningEnv)
        {
            env[key] = value;
        }

        return env;
    }

    // Prepend the MG-1 hardening -c overrides plus the W1-A per-repository neutralizations. They must
    // precede the subcommand, so they lead the arg list.
    private static string[] Hardened(string workingDir, IReadOnlyDictionary<string, string> env, string[] args)
    {
        var neutralize = NeutralizingArgs(workingDir, env);
        var combined = new string[HardeningArgs.Length + neutralize.Length + args.Length];
        Array.Copy(HardeningArgs, combined, HardeningArgs.Length);
        Array.Copy(neutralize, 0, combined, HardeningArgs.Length, neutralize.Length);
        Array.Copy(args, 0, combined, HardeningArgs.Length + neutralize.Length, args.Length);
        return combined;
    }

    /// <summary>
    /// W1-A — the <c>-c &lt;key&gt;=</c> overrides that disable every command-executing config key the
    /// repository at <paramref name="workingDir"/> actually declares.
    ///
    /// <para><b>Why enumeration rather than a pin list.</b> git's dangerous config is mostly
    /// <i>wildcard</i>-shaped: <c>filter.&lt;driver&gt;.clean</c>, <c>diff.&lt;driver&gt;.textconv</c>,
    /// <c>merge.&lt;driver&gt;.driver</c>. The driver name is arbitrary, so no fixed <c>-c</c> list can
    /// cover the family, which is why the previous hardening wrote the family off as unreachable. It is
    /// reachable — but only through a name the config file must itself contain. So the file is read
    /// first (<c>git config --list --name-only</c> executes nothing and, under
    /// <see cref="HardeningEnv"/>, sees only the repo-local, worktree and included scopes), and each
    /// declared key is overridden empty on the command line, which is the highest-precedence scope.
    /// For the <c>filter.*</c> family — the one the audit measured — empty IS git's own "absent":
    /// <c>apply_filter</c> gates on <c>cmd &amp;&amp; *cmd</c>, so a neutralized clean/smudge/process
    /// driver simply does not run and <c>add</c>/<c>commit</c>/<c>checkout</c> behave normally.</para>
    ///
    /// <para><b>The <c>diff.*</c> family behaves differently, and the difference is a deliberate
    /// trade.</b> git reads an empty <c>diff.external</c> / <c>diff.&lt;d&gt;.command</c> /
    /// <c>diff.&lt;d&gt;.textconv</c> as a command to RUN, not as "none" — measured: it reports
    /// <c>error: cannot run :</c> and fails the diff. So neutralizing one of those does not restore the
    /// ordinary diff; it turns the daemon's diff into a loud failure for that repository. That is the
    /// correct side of the trade — the agent's command still never runs, and the alternative is
    /// executing it — and it is contained: only a repository that planted an executable diff driver is
    /// affected, and the product plants none. It is also exactly why <see cref="HardeningArgs"/> does
    /// not pin these unconditionally.</para>
    ///
    /// <para><b>Behaviour-preserving.</b> The daemon writes exactly six repo-local keys
    /// (<c>core.sharedRepository</c>, <c>core.untrackedCache</c>, <c>gc.auto</c>,
    /// <c>maintenance.auto</c>, <c>receive.denyNonFastForwards</c>, <c>receive.denyDeletes</c>) plus
    /// <c>remote.origin.*</c>; none of them is in a command-executing family, and nothing in the agent
    /// platform relies on a filter/textconv/merge driver. So every key this neutralizes is, by
    /// construction, one the daemon did not put there.</para>
    ///
    /// <para><b>A key it cannot express is a refusal, not a pass.</b> A subsection name may legally
    /// contain <c>=</c>, whitespace or control characters (<c>[filter "a=b"]</c>), and
    /// <c>-c filter.a=b.clean=</c> would silently parse as a different key — an override that looks
    /// applied and is not. That is the one failure mode worse than no override at all, so such a key
    /// raises a typed <see cref="RepoProvisioningException"/> and the git call never runs.</para>
    ///
    /// <para><b>Residual.</b> This is a snapshot taken immediately before the spawn, so a concurrently
    /// running agent could in principle write a new driver into the window. The window is closed by
    /// construction on the keep-alive path (the agent is yielded — its jail is paused — for the whole
    /// cycle) and narrowed to a few milliseconds elsewhere. The structural answer is the second layer:
    /// the daemon no longer follows agent-written layout pointers (<see cref="TrustedWorktreeLayout"/>)
    /// and the jail no longer gets a writable <c>.git</c> pointer file.</para>
    /// </summary>
    private static string[] NeutralizingArgs(string workingDir, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrEmpty(workingDir))
        {
            return Array.Empty<string>();
        }

        int code;
        string listing;
        try
        {
            // No -c overrides on the probe itself: listing config executes nothing, and the probe must
            // not recurse into this method.
            (code, listing, _) = GitService.RunGit(
                workingDir, env, CancellationToken.None, "config", "--list", "--name-only", "-z");
        }
        catch (GitOperationException)
        {
            // git missing / not launchable: the real call below raises the typed error the caller
            // expects. Adding no overrides here does not weaken it — there is no git to harden.
            return Array.Empty<string>();
        }

        // Not a repository (clone/worktree-add run from a parent directory), or an empty config: nothing
        // declared means nothing to neutralize.
        if (code != 0 || listing.Length == 0)
        {
            return Array.Empty<string>();
        }

        List<string>? args = null;
        HashSet<string>? seen = null;
        foreach (var raw in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = raw.Trim();
            if (key.Length == 0 || !GitConfigExecutionSurface.IsCommandExecuting(key))
            {
                continue;
            }

            if (!GitConfigExecutionSurface.IsExpressibleOnCommandLine(key))
            {
                throw new RepoProvisioningException(
                    "W1-A: the repository config at '" + workingDir + "' declares the command-executing key "
                    + "'" + GitConfigExecutionSurface.Describe(key) + "', whose name cannot be overridden on "
                    + "git's command line. Refusing to run git against it rather than running it with an "
                    + "override that would silently apply to a different key.");
            }

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(key))
            {
                continue;
            }

            args ??= new List<string>();
            if (seen.Count > MaxNeutralizedKeys)
            {
                throw new RepoProvisioningException(
                    "W1-A: the repository config at '" + workingDir + "' declares more than "
                    + MaxNeutralizedKeys + " command-executing keys. Refusing to run git against it.");
            }

            args.Add("-c");
            args.Add(key + "=");
        }

        return args is null ? Array.Empty<string>() : args.ToArray();
    }

    // The caller's subcommand name for error text (args[0]), skipping the injected -c pairs.
    private static string Subcommand(string[] args) => args.Length > 0 ? args[0] : "git";
}

/// <summary>
/// W1-A — the classifier for "config keys git will execute a command from". Pure, so the whole family
/// list is unit-pinned rather than inferred from a regex buried in a runner.
///
/// <para>A git config key is <c>section.variable</c> or <c>section.&lt;subsection&gt;.variable</c>.
/// git lower-cases the section and the variable but preserves the subsection verbatim, so matching is
/// done on the first and last dot-delimited segments (case-insensitively) and the middle — which may
/// itself contain dots, e.g. <c>credential.https://host.helper</c> — is never interpreted.</para>
/// </summary>
internal static class GitConfigExecutionSurface
{
    /// <summary>
    /// True iff git may spawn a command from <paramref name="key"/>'s value.
    ///
    /// <para>The families, and what runs them: <c>filter.&lt;d&gt;.clean|smudge|process</c> (checkout,
    /// <c>add</c>, <c>status</c>, <c>diff</c> — the vector the audit measured);
    /// <c>diff.&lt;d&gt;.command|textconv|external</c> and bare <c>diff.external</c> (<c>git diff</c>,
    /// which the merge-diff service runs); <c>merge.&lt;d&gt;.driver</c> (every rebase/merge that
    /// touches a path with a <c>merge=</c> attribute); <c>difftool</c>/<c>mergetool</c>/<c>guitool</c>
    /// <c>.cmd|.path</c>; the fixed-name <c>core.*</c>/<c>sequence.editor</c> knobs;
    /// <c>credential.[&lt;url&gt;.]helper</c>; <c>remote.&lt;r&gt;.uploadpack|receivepack|proxy</c>
    /// (fetch/push); <c>uploadpack.packObjectsHook</c>; <c>init.templateDir</c> (hooks by another
    /// name); <c>gpg[.&lt;fmt&gt;].program</c>; <c>trailer.&lt;t&gt;.command|cmd</c>;
    /// <c>submodule.&lt;s&gt;.update</c> (a <c>!command</c> form); and any <c>pager.&lt;cmd&gt;</c>.</para>
    ///
    /// <para>Deliberately NOT here: <c>alias.*</c> — a git alias cannot shadow a built-in subcommand and
    /// the daemon never invokes anything but built-ins, so neutralizing them would forbid something
    /// that cannot happen. <c>include.path</c>/<c>includeIf.*</c> — not executable, and the enumeration
    /// this feeds already lists the keys an include pulled in. <c>http.*.proxy</c> — an exfiltration
    /// question, not an execution one, and out of scope for this control.</para>
    /// </summary>
    internal static bool IsCommandExecuting(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var firstDot = key.IndexOf('.');
        var lastDot = key.LastIndexOf('.');
        if (firstDot <= 0 || lastDot >= key.Length - 1)
        {
            return false;
        }

        var section = key[..firstDot].ToLowerInvariant();
        var variable = key[(lastDot + 1)..].ToLowerInvariant();
        var hasSubsection = lastDot > firstDot;

        return section switch
        {
            "filter" => hasSubsection && variable is "clean" or "smudge" or "process",
            "merge" => hasSubsection && variable is "driver",
            "diff" => variable is "command" or "textconv" or "external",
            "difftool" or "mergetool" or "guitool" => variable is "cmd" or "path",
            // core.attributesFile is deliberately absent: it names a file, not a command. It can ASSIGN a
            // driver, but every driver it could assign is itself neutralized above, and pinning it empty
            // would be another unmeasured "empty means disabled" assumption.
            "core" => variable is "hookspath" or "fsmonitor" or "pager" or "editor" or "sshcommand"
                or "askpass" or "gitproxy" or "alternaterefscommand",
            "sequence" => variable is "editor",
            "credential" => variable is "helper",
            "remote" => hasSubsection && variable is "uploadpack" or "receivepack" or "proxy",
            "uploadpack" => variable is "packobjectshook",
            "init" => variable is "templatedir",
            "gpg" => variable is "program",
            "trailer" => hasSubsection && variable is "command" or "cmd",
            "submodule" => hasSubsection && variable is "update",
            "browser" => variable is "cmd" or "path",
            "pager" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True iff <paramref name="key"/> can be overridden verbatim by <c>git -c &lt;key&gt;=</c>.
    ///
    /// <para><c>-c</c> splits the argument at the FIRST <c>=</c>, so a key whose (legal) subsection name
    /// contains one would be parsed as a shorter key with a value — an override that appears to apply
    /// and does not. Whitespace and control characters are refused for the same reason: they make the
    /// argument's parse depend on something other than the key.</para>
    /// </summary>
    internal static bool IsExpressibleOnCommandLine(string key)
    {
        foreach (var c in key)
        {
            if (c == '=' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return key.Length > 0;
    }

    /// <summary>A key rendered safe for an exception message: control characters escaped, length capped
    /// (the name is agent-controlled text on its way into a daemon log).</summary>
    internal static string Describe(string key)
    {
        var trimmed = key.Length > 120 ? key[..120] + "…" : key;
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            sb.Append(char.IsControl(c) ? '?' : c);
        }

        return sb.ToString();
    }
}

/// <summary>
/// W1-A layer 2 — the git layout the daemon will use for one agent worktree, derived from
/// daemon-computed roots and <b>validated</b>, never simply discovered.
///
/// <para><b>The vector.</b> A linked worktree's <c>.git</c> is a one-line file
/// (<c>gitdir: &lt;abs path&gt;</c>) inside the agent's writable workspace, and the per-worktree gitdir
/// it names holds a <c>commondir</c> file that in turn names where <c>refs/</c>, <c>objects/</c> and
/// <c>config</c> come from. Both are agent-writable. A daemon that runs <c>git</c> with the worktree as
/// its working directory follows both, so an agent can point the daemon's git at a different
/// repository's config — the read-only shared mirror being the obvious target, since every co-tenant
/// reads it — and the daemon then operates on refs it never meant to touch.</para>
///
/// <para><b>What is trusted.</b> The working tree path (the daemon computed it), and — when supplied —
/// the agent repository root (the daemon computed that too, from
/// <see cref="AgentRepoLayout.AgentRepoPath"/>). Nothing else. <c>commondir</c> is never read: the
/// common directory is derived from the gitdir's own <c>…/worktrees/&lt;name&gt;</c> shape, and the
/// resolved layout is pinned into the child process with <c>GIT_DIR</c>, <c>GIT_COMMON_DIR</c> and
/// <c>GIT_WORK_TREE</c>, which outrank every on-disk pointer.</para>
/// </summary>
internal sealed class TrustedWorktreeLayout
{
    private TrustedWorktreeLayout(string workTree, string gitDir, string commonDir)
    {
        WorkTree = workTree;
        GitDir = gitDir;
        CommonDir = commonDir;
        Env = new Dictionary<string, string>
        {
            ["GIT_DIR"] = gitDir,
            ["GIT_COMMON_DIR"] = commonDir,
            ["GIT_WORK_TREE"] = workTree,
        };
    }

    /// <summary>The agent's working tree — the process working directory for every pinned call.</summary>
    internal string WorkTree { get; }

    /// <summary>The per-worktree git directory (<c>&lt;repo&gt;/worktrees/&lt;name&gt;</c>).</summary>
    internal string GitDir { get; }

    /// <summary>The common git directory — refs, objects, config. Derived, never read from
    /// <c>commondir</c>.</summary>
    internal string CommonDir { get; }

    /// <summary>The pin, as environment. Passed to <see cref="AgentGitCommand.RunWithEnv"/>.</summary>
    internal IReadOnlyDictionary<string, string> Env { get; }

    /// <summary>
    /// Resolves and validates the layout for <paramref name="worktreePath"/>, or returns null when it
    /// cannot be established. <b>Null is "run unpinned, exactly as before"</b>, not "unsafe": the
    /// substrate-less test doubles and the pre-MG-3 shapes (a plain <c>.git</c> directory) have no
    /// linked-worktree indirection to be redirected through in the first place.
    /// </summary>
    /// <param name="worktreePath">The daemon-computed working tree.</param>
    /// <param name="agentRepoPath">The daemon-computed per-agent repository, when the caller knows it.
    /// Supplied ⇒ the common directory is asserted to be exactly this, so the <c>gitdir:</c> pointer is
    /// only ever used to select <i>which</i> registered worktree we are, never <i>whose</i> repository.
    /// Null ⇒ the common directory is derived from the pointer's own shape and checked against
    /// <paramref name="forbiddenCommonDir"/>.</param>
    /// <param name="forbiddenCommonDir">A path the common directory must not be — the shared mirror.
    /// This is the exact redirect the audit called out as "a plausible second vector".</param>
    internal static TrustedWorktreeLayout? TryResolve(
        string worktreePath, string? agentRepoPath = null, string? forbiddenCommonDir = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        // RealPath, not GetFullPath: git writes the SYMLINK-RESOLVED path into the pointer file and into
        // its own worktrees/<n>/gitdir registration, while the daemon's computed paths are whatever the
        // substrate handed it. On macOS — where the daemon runs on the host — /tmp and /var are symlinks
        // into /private, so every comparison below would have been between two spellings of the same
        // directory and every legitimate worktree would have been refused as "a repository the agent
        // chose". Measured by TrustedWorktreeLayoutTests, which failed on exactly that before this line.
        var workTree = RealPath(worktreePath);
        var dotGit = Path.Combine(workTree, ".git");

        // A real .git directory is the main working tree: there is no pointer to subvert, and pinning
        // GIT_DIR would only restate what git would find. Leave it unpinned.
        if (Directory.Exists(dotGit) || !File.Exists(dotGit))
        {
            return null;
        }

        string pointer;
        try
        {
            pointer = File.ReadAllText(dotGit).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var target = pointer[prefix.Length..].Trim();
        if (target.Length == 0)
        {
            return null;
        }

        var gitDir = RealPath(Path.IsPathRooted(target) ? target : Path.Combine(workTree, target));
        var worktreesDir = Path.GetDirectoryName(gitDir);
        var commonDir = worktreesDir is null ? null : Path.GetDirectoryName(worktreesDir);

        // The pointer must have the shape git itself writes: <common>/worktrees/<name>. Anything else is
        // not a linked worktree of anything, and we refuse to guess what it is.
        if (commonDir is null || worktreesDir is null ||
            !string.Equals(Path.GetFileName(worktreesDir), "worktrees", StringComparison.Ordinal))
        {
            throw new RepoProvisioningException(
                $"W1-A: the worktree at '{workTree}' points its gitdir at '{gitDir}', which is not a linked "
                + "worktree directory of any repository. Refusing to run daemon-side git against it.");
        }

        // The mirror is checked FIRST, ahead of the identity check that would also catch it: it is the
        // named vector, and "you aimed this at the shared mirror" is the diagnostic an operator can act
        // on, where "this is not the repository we provisioned" is merely true.
        if (forbiddenCommonDir is { Length: > 0 } && PathsEqual(commonDir, RealPath(forbiddenCommonDir)))
        {
            throw new RepoProvisioningException(
                $"W1-A: the worktree at '{workTree}' points its git directory at the shared mirror "
                + $"'{commonDir}'. That mirror is read-only to every jail precisely so no agent can reach "
                + "it; refusing to reach it on the agent's behalf.");
        }

        if (agentRepoPath is { Length: > 0 } && !PathsEqual(commonDir, RealPath(agentRepoPath)))
        {
            throw new RepoProvisioningException(
                $"W1-A: the worktree at '{workTree}' claims to belong to '{commonDir}', but the daemon "
                + $"provisioned it under '{RealPath(agentRepoPath)}'. Refusing to run daemon-side "
                + "git against a repository the agent chose.");
        }

        // Round-trip: the repository must itself have this worktree registered, and register it back at
        // the path we were given. A pointer at a directory that does not name us back is not our layout.
        var registration = Path.Combine(gitDir, "gitdir");
        try
        {
            if (!File.Exists(registration) || !PathsEqual(RealPath(File.ReadAllText(registration).Trim()), dotGit))
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return new TrustedWorktreeLayout(workTree, gitDir, commonDir);
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(a, b, comparison);
    }

    /// <summary>
    /// The absolute, <b>symlink-resolved</b>, separator-trimmed form of a path — the only spelling in
    /// which two of these may be compared.
    ///
    /// <para>.NET has no <c>realpath</c>: <see cref="Path.GetFullPath(string)"/> normalizes <c>..</c> and
    /// makes a path absolute but resolves no links, and <c>ResolveLinkTarget</c> resolves only the FINAL
    /// component. An intermediate link is the common case on the substrate this actually runs on — macOS
    /// puts the daemon on the host, where <c>/tmp</c> and <c>/var</c> are links into <c>/private</c> —
    /// so the walk is component-by-component from the root. A component that does not exist, or that
    /// cannot be inspected, is kept verbatim; this is a canonicalizer, never a validator.</para>
    /// </summary>
    private static string RealPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return Path.TrimEndingDirectorySeparator(full);
        }

        var current = root;
        foreach (var segment in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                var link = Directory.Exists(current)
                    ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    : File.Exists(current)
                        ? new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        : null;
                if (!string.IsNullOrEmpty(link))
                {
                    current = Path.IsPathRooted(link)
                        ? link
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, link));
                }
            }
            catch (IOException)
            {
                // An unreadable component is kept as written — the comparison then simply fails closed.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
