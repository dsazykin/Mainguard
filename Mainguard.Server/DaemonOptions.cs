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
    /// Dev-only merge-queue seeding (docs/design/queue-seeding.md §7). Captured from
    /// <c>MAINGUARD_ENABLE_QUEUE_SEEDING</c> ONCE, at options construction — i.e. at process startup —
    /// and never re-read: the whole gate is "was this daemon STARTED as a seeding daemon". Off (the
    /// only value a shipped build ever sees) leaves <c>QueueSeedingService</c> unmapped entirely.
    /// The in-proc test tier flips it per host by replacing the <c>QueueSeedingOptions</c> singleton
    /// (<c>DaemonFixture.EnableQueueSeeding</c>) — a process-wide env var cannot vary between two
    /// test daemons in one process.
    /// </summary>
    public bool QueueSeedingEnabled { get; init; }
        = Environment.GetEnvironmentVariable("MAINGUARD_ENABLE_QUEUE_SEEDING") == "1";

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
    /// <para><b>ON by default (MG-4 item 3).</b> It used to be off, settable only through
    /// <c>MAINGUARD_GATEWAY_BIND</c>, which nothing in the repo ever set — so in every supported
    /// deployment a BYOK jail received the raw provider API key, which is the thing MG-4 exists to stop,
    /// and it mattered most for agents spawned from untrusted external-PR content. The default is now
    /// <see cref="AutoBind"/>: the daemon picks a private host address the jails' egress proxy can dial.
    /// Set <c>MAINGUARD_GATEWAY_BIND=off</c> (or <c>--gateway-bind off</c>) to restore the old posture,
    /// or name an explicit address to pin it.</para>
    ///
    /// <para><b>Turning it on cannot break a working agent, and that is enforced rather than hoped.</b>
    /// Confinement engages only for a BYOK agent (a key was supplied) whose CLI declares both a
    /// base-URL variable and a model host, <i>and</i> only once the jail's own egress proxy has been
    /// measured able to reach this address (<c>IEgressPolicy.CanProxyReachAsync</c>). Any of those
    /// failing leaves the spawn exactly as it was before the gateway existed. An interactive-OAuth agent
    /// holds no key, so it is never confined and never touches the gateway at all.</para>
    /// </summary>
    public string? GatewayBindAddress { get; init; }
        = ResolveBindAddress(Environment.GetEnvironmentVariable("MAINGUARD_GATEWAY_BIND"));

    /// <summary>The sentinel meaning "pick a private host address the agents' egress proxy can reach".</summary>
    public const string AutoBind = "auto";

    /// <summary>The sentinel meaning "no gateway listener" — the pre-MG-4 posture.</summary>
    public const string DisabledBind = "off";

    /// <summary>
    /// Turns a configured bind value (or the absence of one) into the address Kestrel binds, or null for
    /// "disabled".
    ///
    /// <para>An explicit address is passed through untouched, so <see cref="Gateway.GatewayBindPolicy"/>
    /// still gets to refuse a wildcard or public one loudly. <c>off</c> disables. Anything else —
    /// including the default of no configuration at all — auto-resolves.</para>
    ///
    /// <para><b>Auto-resolution failing yields null, not an exception.</b> A host with no private IPv4
    /// has nowhere to put a gateway a container can reach; refusing to start the daemon over that would
    /// turn a missing OPTIMISATION into an outage, so the gateway is simply absent and every spawn
    /// behaves as it did before.</para>
    /// </summary>
    internal static string? ResolveBindAddress(string? configured)
    {
        var value = configured?.Trim();
        if (string.Equals(value, DisabledBind, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(value) && !string.Equals(value, AutoBind, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return Gateway.GatewayBindPolicy.TryResolvePrivateHostAddress();
    }

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
                // MG-4/MG-13: turning the model gateway on. Previously settable ONLY through
                // MAINGUARD_GATEWAY_BIND, which nothing in the repo ever set — so the listener was
                // unreachable by every supported way of starting the daemon (systemd unit, installer,
                // dev loop) and the confinement it enables could not be switched on at all.
                case "--gateway-bind":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException(
                            "--gateway-bind requires a private or loopback IP address, 'auto', or 'off' "
                            + "(never a wildcard).");
                    }

                    // Routed through the same resolver as the environment variable so 'auto' and 'off'
                    // mean the same thing from either source — a flag that silently meant something
                    // different from the env var would be its own defect.
                    options = options with { GatewayBindAddress = ResolveBindAddress(args[i + 1]) };
                    i++;
                    break;
                case "--gateway-port":
                    // Same loudness rule as --port: an unparseable value must not leave the gateway
                    // silently on a different port than the operator believes.
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], out var gatewayPort)
                        || gatewayPort is < 1 or > 65535)
                    {
                        throw new ArgumentException(
                            "--gateway-port requires a TCP port number (1-65535); got "
                            + $"'{(i + 1 < args.Length ? args[i + 1] : "<nothing>")}'.");
                    }

                    options = options with { GatewayPort = gatewayPort };
                    i++;
                    break;
            }
        }

        return options;
    }
}
