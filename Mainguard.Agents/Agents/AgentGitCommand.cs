using System;
using System.Collections.Generic;
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
/// runs git against both (worktree add/remove/prune, the mediated fetch, status). The hardening
/// therefore stays exactly as load-bearing as it was, on a smaller surface. The residual
/// repo-local <c>filter.*</c> vector — which env/<c>-c</c> alone cannot neutralize — is likewise
/// confined to the per-agent repo, whose contents that agent already controls.
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
    /// </list>
    /// </summary>
    private static readonly string[] HardeningArgs =
    {
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.fsmonitor=",
        "-c", "protocol.ext.allow=never",
    };

    /// <summary>
    /// Environment forced on every daemon-side git: drop system config (<c>/etc/gitconfig</c>) and
    /// point the global config at nothing, so only the (overridden) repo-local config can apply.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> HardeningEnv = new Dictionary<string, string>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
    };

    /// <summary>Runs git in <paramref name="workingDir"/>; throws typed on a non-zero exit. Returns stdout.</summary>
    internal static string Run(string workingDir, params string[] args)
    {
        var (code, output, err) = GitService.RunGit(workingDir, HardeningEnv, CancellationToken.None, Hardened(args));
        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
            throw new RepoProvisioningException($"git {Subcommand(args)} failed (exit {code}): {detail}");
        }

        return output;
    }

    /// <summary>
    /// <see cref="Run"/> with extra environment merged OVER <see cref="HardeningEnv"/> — for the one
    /// caller that needs a scratch <c>GIT_INDEX_FILE</c> (the queue seeder's plumbing commits,
    /// docs/design/queue-seeding.md §2). The hardening pins stay: the extra env is additive and the
    /// hardening keys are re-applied last, so a caller cannot un-pin them.
    /// </summary>
    internal static string RunWithEnv(
        string workingDir, IReadOnlyDictionary<string, string> extraEnv, params string[] args)
    {
        var env = new Dictionary<string, string>(extraEnv);
        foreach (var (key, value) in HardeningEnv)
        {
            env[key] = value;
        }

        var (code, output, err) = GitService.RunGit(workingDir, env, CancellationToken.None, Hardened(args));
        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
            throw new RepoProvisioningException($"git {Subcommand(args)} failed (exit {code}): {detail}");
        }

        return output;
    }

    /// <summary>Runs git and returns the raw exit code without throwing (for probe-style checks).</summary>
    internal static int TryRun(string workingDir, out string output, params string[] args)
    {
        var (code, stdout, _) = GitService.RunGit(workingDir, HardeningEnv, CancellationToken.None, Hardened(args));
        output = stdout;
        return code;
    }

    // Prepend the MG-1 hardening -c overrides. They must precede the subcommand, so they lead the arg list.
    private static string[] Hardened(string[] args)
    {
        var combined = new string[HardeningArgs.Length + args.Length];
        Array.Copy(HardeningArgs, combined, HardeningArgs.Length);
        Array.Copy(args, 0, combined, HardeningArgs.Length, args.Length);
        return combined;
    }

    // The caller's subcommand name for error text (args[0]), skipping the injected -c pairs.
    private static string Subcommand(string[] args) => args.Length > 0 ? args[0] : "git";
}
