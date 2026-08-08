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
public static class SandboxCliLaunch
{
    /// <summary>The docker CLI binary the daemon spawns under its PTY.</summary>
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
        return (DockerBinary, args);
    }
}
