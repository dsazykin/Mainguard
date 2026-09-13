using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The pure argv builder for launching an installed agent CLI inside its hardened jail under a real
/// TTY: <c>docker exec -it</c> run by the daemon (which lives in the same VM as dockerd) under a
/// daemon-side PTY (<see cref="PtyProcessShim"/> forkpty), so <c>isatty()</c> is true inside the
/// jail, resize propagates (the docker CLI forwards SIGWINCH to the exec's TTY), and Ctrl+C reaches
/// the CLI. The command is exec'd directly — <c>docker</c> with its arguments, never a host shell.
///
/// <para><b>The in-container wrapper</b> is a fixed, Mainguard-owned <c>sh</c> script taking the CLI
/// argv purely as positional <c>"$@"</c> arguments (the same pattern the sandbox engine's secret
/// writer uses — no user data is ever interpolated into script text). It does exactly three things
/// before <c>exec</c>-ing the CLI: source the agent-owned P2-01 credential file (so the adapter's
/// <c>apiKeyEnvVar</c> injection works — the daemon host can never read that tmpfs, so <c>-e</c>
/// argv injection is both impossible and a G-13 violation), put the read-only agent-IPC mount on
/// PATH when present (the coordinator's <c>mainguard-agent</c> spawn shim), and hand off with
/// <c>exec</c> so the CLI is the TTY's foreground process.</para>
/// </summary>
/// <summary>
/// <b>Where the docker CLI is allowed to come from (audit F54).</b>
///
/// <para><b>What went wrong.</b> Every daemon-side docker invocation named the bare string
/// <c>"docker"</c>, which <see cref="System.Diagnostics.Process"/> resolves against the daemon's
/// INHERITED <c>PATH</c>. The daemon is the component that creates jails, writes the shared adapters
/// tree, and — on the macOS substrate — runs installs; whoever can prepend a directory to the
/// environment it was started with therefore chooses the program that does all of that. Every other
/// executable this codebase hands to a privileged facility goes through
/// <see cref="Bootstrap.TrustedExecutablePath"/>; the one binary the whole sandbox boundary is built
/// out of did not.</para>
///
/// <para><b>The rule.</b> Resolve to an ABSOLUTE path from a fixed list of system-owned directories,
/// in order, and use the first that exists. No <c>PATH</c> lookup, and deliberately no environment
/// override: an override variable is the same primitive as <c>PATH</c> wearing a different name. The
/// list contains only directories a normal user cannot write (notably NOT <c>~/.docker/bin</c>, which
/// Docker Desktop offers and which is same-user-writable).</para>
///
/// <para><b>When nothing is found</b> the first candidate is returned anyway, so the answer is always an
/// absolute path and the spawn fails with a plain "no such file" — the degrade
/// <c>AgentCliBinder.TryBind</c> already audits on a box with no docker CLI. Returning the bare name as
/// a fallback would reinstate exactly the lookup this class exists to remove.</para>
/// </summary>
public static class TrustedDockerBinary
{
    /// <summary>The system-owned directories a docker CLI may be run from, in preference order.</summary>
    public static IReadOnlyList<string> SearchPath { get; } = OperatingSystem.IsWindows()
        ? new[]
        {
            @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
            @"C:\ProgramData\DockerDesktop\version-bin\docker.exe",
        }
        : new[]
        {
            "/usr/local/bin/docker",
            "/usr/bin/docker",
            "/bin/docker",
            // Docker Desktop for Mac's own copy, and Homebrew's prefix on Apple Silicon. Both are
            // root-owned on a normal install; both are needed because a Mac may have either.
            "/Applications/Docker.app/Contents/Resources/bin/docker",
            "/opt/homebrew/bin/docker",
        };

    private static readonly Lazy<string> Resolved = new(Pick, isThreadSafe: true);

    /// <summary>The absolute docker path this host uses. Resolved once per process.</summary>
    public static string Resolve() => Resolved.Value;

    /// <summary>The <c>PATH</c> a docker child process is given — the same fixed directories, never the
    /// daemon's inherited one. docker still needs a PATH for its credential helpers and CLI plugins, so
    /// this replaces it with a trusted value rather than removing it.</summary>
    public static string TrustedChildPath { get; } = string.Join(
        OperatingSystem.IsWindows() ? ';' : ':',
        SearchPath.Select(DirectoryOf).Distinct(StringComparer.Ordinal));

    private static string Pick()
    {
        foreach (var candidate in SearchPath)
        {
            try
            {
                if (System.IO.File.Exists(candidate))
                    return candidate;
            }
            catch (Exception)
            {
                // An unreadable directory is not this candidate — keep looking.
            }
        }

        return SearchPath[0];
    }

    private static string DirectoryOf(string path)
    {
        var cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut <= 0 ? path : path[..cut];
    }
}

public static class SandboxCliLaunch
{
    /// <summary>The docker CLI binary's NAME. Kept for the log/diagnostic sentences that talk about
    /// "the docker CLI"; what actually gets executed is <see cref="TrustedDockerBinary.Resolve"/>, an
    /// absolute path — see that type for why a bare name was a hole rather than a convenience.</summary>
    public const string DockerBinary = "docker";

    /// <summary>The terminal type advertised to the CLI on BOTH sides of the exec: the daemon-side
    /// PTY the docker CLI runs under, and (via <c>-e</c>) the in-jail environment the agent CLI
    /// reads. One constant so the two can never drift.</summary>
    public const string InJailTerm = "xterm-256color";

    /// <summary>
    /// The fixed in-container launcher script (argv-safe: the CLI command arrives as "$@").
    /// Kept single-quoted-safe: the text contains no single quotes and no interpolation.
    /// </summary>
    /// <remarks>The credential path is <see cref="CredTmpfsSpec.DefaultCredentialPath"/> spelled
    /// through the constant, not copied: it moved into the agent uid's OWN tmpfs directory when the
    /// impossible in-jail chown was removed, and a second hand-written copy of the path is exactly how
    /// a wrapper ends up sourcing a file that no longer exists — silently, since the guard is
    /// <c>[ -r … ]</c>.</remarks>
    public const string WrapperScript =
        "if [ -r " + CredTmpfsSpec.DefaultCredentialPath + " ]; then set -a; . "
        + CredTmpfsSpec.DefaultCredentialPath + "; set +a; fi; "
        + "if [ -d " + Ipc.AgentIpcPaths.SandboxMount + " ]; then PATH=\"" + Ipc.AgentIpcPaths.SandboxMount + ":$PATH\"; export PATH; fi; "
        + "exec \"$@\"";

    /// <summary>
    /// Builds the full daemon-side argv (command + args) that starts <paramref name="launch"/> inside
    /// <paramref name="containerId"/> attached to an interactive TTY, running as the agent uid in the
    /// workspace. Throws on an empty launch command — a jail with no CLI is attached as a shell-less
    /// session-only record, never a fabricated exec.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Args) BuildDockerExecArgv(
        string containerId, IReadOnlyList<string> launch, int agentUid)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("A container id is required.", nameof(containerId));
        }

        if (launch is null || launch.Count == 0 || launch.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A non-empty CLI launch argv is required.", nameof(launch));
        }

        var args = new List<string>
        {
            "exec",
            "-i",
            "-t",
            // A sane TERM INSIDE the jail, explicitly: docker's implicit tty-exec default is bare
            // "xterm", which under-advertises capabilities to full-screen CLI TUIs (and depends on
            // the engine version). The interactive-login flow must never hinge on that implicit.
            "-e", "TERM=" + InJailTerm,
            "-u", agentUid.ToString(CultureInfo.InvariantCulture),
            "-w", ContainerSpecBuilder.WorkspaceTarget,
            containerId,
            "sh", "-c", WrapperScript, "mainguard-launch",
        };
        args.AddRange(launch);
        // Audit F54: the absolute, allow-listed path — never the bare name resolved out of whatever
        // PATH the daemon inherited.
        return (TrustedDockerBinary.Resolve(), args);
    }
}
