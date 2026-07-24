using System;

namespace Mainguard.Server;

/// <summary>
/// Parsed daemon launch options. Transport-agnostic and loopback-only by construction
/// (there is no "bind address" knob — the host always binds 127.0.0.1, invariant 2).
/// </summary>
public sealed record DaemonOptions
{
    /// <summary>The default loopback port for the local-dev / CI daemon.</summary>
    public const int DefaultPort = 5250;

    /// <summary>Loopback TCP port to bind.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Run directly on Windows/localhost (no WSL) for the dev loop and CI. Linux-only
    /// subsystems (forkpty, cgroups) are guarded behind capability checks and simply
    /// absent here — they arrive in later tasks.
    /// </summary>
    public bool LocalDev { get; init; }

    /// <summary>
    /// Start, self-probe over the loopback gRPC endpoint, and exit 0 on success — the
    /// CI <c>--local-dev</c> smoke. Prints nothing on success.
    /// </summary>
    public bool Smoke { get; init; }

    /// <summary>Override the session-token file path (test isolation). Null → OS default.</summary>
    public string? TokenPath { get; init; }

    /// <summary>Override the daemon SQLite path (P2-08 spend ledger; test isolation). Null → derived.</summary>
    public string? DataPath { get; init; }

    /// <summary>
    /// P2-18 terminal target engine flag: <c>libvterm</c> or <c>interim</c> (the default until the
    /// P2-04 parity sign-off flips it). <c>--terminal-engine</c> overrides the
    /// <c>MAINGUARD_TERMINAL_ENGINE</c> environment variable; unset/unknown → interim. A libvterm
    /// request degrades to interim where the native library is absent (Windows local-dev).
    /// </summary>
    public string? TerminalEngine { get; init; }
        = Environment.GetEnvironmentVariable("MAINGUARD_TERMINAL_ENGINE");

    /// <summary>
    /// MG-13/MG-4: the address the MODEL GATEWAY listener binds, or null (default) to leave it
    /// disabled. The gRPC control plane is always loopback-only and is unaffected by this.
    ///
    /// <para>The gateway needs its own listener because the agent jail sits on an
    /// <c>Internal=true</c> Docker network where <c>127.0.0.1</c> is the container itself — a gateway
    /// on loopback is unreachable by the agents it exists to front, so MG-4's key confinement cannot
    /// work without one. <see cref="Gateway.GatewayBindPolicy"/> constrains this to loopback or a
    /// private address and refuses a wildcard or public bind.</para>
    ///
    /// <para>Disabled by default, deliberately: with no gateway there is nothing to redirect a CLI to,
    /// so the spawn path keeps injecting the provider key exactly as before. Enabling the gateway is
    /// what turns on the confinement.</para>
    /// </summary>
    public string? GatewayBindAddress { get; init; }
        = Environment.GetEnvironmentVariable("MAINGUARD_GATEWAY_BIND");

    /// <summary>The model-gateway listener port (only used when <see cref="GatewayBindAddress"/> is set).</summary>
    public int GatewayPort { get; init; } = DefaultGatewayPort;

    /// <summary>Default model-gateway port — distinct from the gRPC control port.</summary>
    public const int DefaultGatewayPort = 5251;

    public static DaemonOptions Parse(string[] args)
    {
        var options = new DaemonOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-dev":
                    options = options with { LocalDev = true };
                    break;
                case "--smoke":
                    options = options with { Smoke = true };
                    break;
                case "--port":
                    // Loud, not silent: an unparseable --port used to be ignored, leaving the daemon
                    // on the default port while the caller believed otherwise.
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], out var port)
                        || port is < 1 or > 65535)
                    {
                        throw new ArgumentException(
                            $"--port requires a TCP port number (1-65535); got '{(i + 1 < args.Length ? args[i + 1] : "<nothing>")}'.");
                    }

                    options = options with { Port = port };
                    i++;
                    break;
                case "--terminal-engine":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--terminal-engine requires a value (libvterm|interim).");
                    }

                    options = options with { TerminalEngine = args[i + 1] };
                    i++;
                    break;
            }
        }

        return options;
    }
}
