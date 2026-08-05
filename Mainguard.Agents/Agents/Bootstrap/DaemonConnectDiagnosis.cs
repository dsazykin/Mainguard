using System;
using Mainguard.Agents.Daemon;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Which leg of the app→daemon connect path was found broken, established by <b>checking that leg</b>
/// — never inferred from a neighbouring symptom.
///
/// <para>The connect path has four independent legs, and before this existed every one of them
/// surfaced as the same sentence ("Mainguard OS daemon isn't reachable"): the distro may be stopped,
/// the <c>mainguardd</c> process may be down inside a running distro, the process may be up without
/// having published a session, and the session may be published by a daemon too old to speak the
/// mutual TLS this build requires. Those need four different actions, so they get four different
/// verdicts. Anything the probe could not attribute stays <see cref="Undiagnosed"/> and reports the
/// raw failure rather than picking a plausible-sounding cause.</para>
/// </summary>
public enum DaemonConnectStage
{
    /// <summary>The daemon answered an authenticated RPC — nothing is wrong.</summary>
    Reachable,

    /// <summary>MainguardEnv is not in <c>wsl --list --running</c>.</summary>
    DistroNotRunning,

    /// <summary>The distro is running, but <c>pgrep -x mainguardd</c> found no process.</summary>
    DaemonProcessNotRunning,

    /// <summary>The process is up, but no readable <c>daemon.token</c> exists at any probed path.</summary>
    NoSessionToken,

    /// <summary>A session token exists, but the mTLS material this build requires is not beside it —
    /// the signature of a daemon installed before MG-19 pinned the control plane. Repairable by
    /// redeploying the daemon payload this app ships.</summary>
    TransportCredentialsMissing,

    /// <summary>Token and credentials are present, but the port did not accept the call.</summary>
    NotListening,

    /// <summary>The daemon answered and refused this app's credentials.</summary>
    TokenRejected,

    /// <summary>Nothing above matched; the raw failure is carried verbatim in the detail.</summary>
    Undiagnosed,
}

/// <summary>
/// One connect-path verdict: the leg that failed plus the observation that established it. The
/// <see cref="Banner"/> is what the shell shows, and it is deliberately built from
/// <see cref="Detail"/> — the text a user reads names the check that ran, so no message can outrun
/// the evidence behind it.
/// </summary>
/// <param name="Stage">The leg found broken.</param>
/// <param name="Detail">What the probe actually observed (empty when reachable).</param>
public sealed record DaemonConnectDiagnosis(DaemonConnectStage Stage, string Detail)
{
    /// <summary>The everything-answered verdict.</summary>
    public static DaemonConnectDiagnosis Reachable { get; } = new(DaemonConnectStage.Reachable, string.Empty);

    /// <summary>True when the daemon answered.</summary>
    public bool IsReachable => Stage == DaemonConnectStage.Reachable;

    /// <summary>
    /// True only for <see cref="DaemonConnectStage.TransportCredentialsMissing"/> — the one verdict the
    /// app can act on itself, by redeploying the daemon build it ships. Every other leg needs either
    /// time (still booting) or a decision this code must not make on the user's behalf.
    /// </summary>
    public bool IsRepairableByDaemonRefresh => Stage == DaemonConnectStage.TransportCredentialsMissing;

    /// <summary>The persistent degraded-entry banner: what is wrong, what was checked, what to do.</summary>
    public string Banner => Stage switch
    {
        DaemonConnectStage.Reachable => string.Empty,

        DaemonConnectStage.DistroNotRunning =>
            $"Mainguard OS isn't running — the {WslCommands.DistroName} environment did not start within the "
            + $"startup budget. {Detail} Agent features stay unavailable until it does.",

        DaemonConnectStage.DaemonProcessNotRunning =>
            $"The Mainguard OS daemon isn't running — {WslCommands.DistroName} is up, but no mainguardd "
            + $"process was found inside it. {Detail}",

        DaemonConnectStage.NoSessionToken =>
            "The Mainguard OS daemon hasn't published a session yet — mainguardd is running inside "
            + $"{WslCommands.DistroName}, but no session token was readable. {Detail}",

        DaemonConnectStage.TransportCredentialsMissing =>
            "The Mainguard OS daemon is older than this Mainguard build — it published a session token "
            + "but not the mutually-authenticated TLS credentials this build requires, so the app will "
            + $"not connect to it. {Detail}",

        DaemonConnectStage.NotListening =>
            "The Mainguard OS daemon isn't accepting connections yet — its session is published, but the "
            + $"call to 127.0.0.1:{DaemonPaths.DefaultLoopbackPort} did not complete. {Detail}",

        DaemonConnectStage.TokenRejected =>
            "The Mainguard OS daemon rejected this app's session credentials — it has probably restarted "
            + $"since they were read. {Detail}",

        _ => $"Mainguard OS daemon isn't reachable. {Detail}",
    };
}

/// <summary>The result of the one repair the startup sequence may attempt on its own.</summary>
/// <param name="Repaired">True when the daemon payload was redeployed and the unit restarted.</param>
/// <param name="Detail">What happened, for the banner and the oobe.log breadcrumb.</param>
public sealed record DaemonRepairOutcome(bool Repaired, string Detail);
