using System;
using System.IO;
using Mainguard.Server;
using Mainguard.Server.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// P2-15: `mainguardd audit verify` — an offline CLI verb, dispatched before daemon options so it
// can never bind a port or touch the token. WebApplicationFactory hosts pass no args, so the
// in-proc test tier never lands here.
if (args.Length > 0 && string.Equals(args[0], "audit", StringComparison.Ordinal))
{
    return Mainguard.Server.Cli.AuditCommands.Run(args);
}

var options = DaemonOptions.Parse(args);

// --local-dev --smoke: start, self-probe, exit 0 (prints nothing on success).
if (options.Smoke)
{
    return await DaemonHost.RunSmokeAsync(options);
}

// Normal daemon run. Build via the shared host configuration so the in-proc test
// tier (WebApplicationFactory<Program>) exercises the same pipeline. app.Run() is
// reached so the test harness can intercept host startup.
// F55: the single-instance guard fires during Build (the Kestrel options callback takes the lock before
// a listener is configured), so the refusal is caught around Build as well as around Run. It is the guard
// working, not a crash — one line on stderr and a named exit code, no stack trace. Nothing has been
// written: the credential files and the migration lock still belong to the daemon that is up.
Microsoft.AspNetCore.Builder.WebApplication app;
try
{
    app = DaemonHost.Build(options);
}
catch (Mainguard.Server.Runtime.DaemonAlreadyRunningException already)
{
    Console.Error.WriteLine(already.Message);
    return DaemonExitCodes.RefusedStart();
}

try
{
    app.Run();
}
catch (Mainguard.Server.Runtime.DaemonAlreadyRunningException already)
{
    try
    {
        app.Services.GetService<ILoggerFactory>()?
            .CreateLogger(DaemonLogCategories.Lifecycle)
            .LogWarning("refusing to start: {Message}", already.Message);
    }
    catch (Exception)
    {
        // Diagnostics must never mask the refusal they diagnose.
    }

    Console.Error.WriteLine(already.Message);
    return DaemonExitCodes.RefusedStart();
}
catch (IOException ex)
{
    // Loopback port already bound → typed failure naming the port (edge row 3). Record it under the
    // Lifecycle log first so the outage is diagnosable from lifecycle.log/journal, not only from the
    // .NET crash dump — guarded so a logging hiccup never masks the real bind failure.
    try
    {
        app.Services.GetService<ILoggerFactory>()?
            .CreateLogger(DaemonLogCategories.Lifecycle)
            .LogCritical(ex, "bind failed port={Port}: {Message}", options.Port, ex.Message);
    }
    catch (Exception)
    {
        // Diagnostics must never mask the failure they diagnose.
    }

    Console.Error.WriteLine(DaemonHost.BindFailureMessage(options.Port));
    throw new DaemonStartupException(options.Port, DaemonHost.BindFailureMessage(options.Port), ex);
}

return 0;

/// <summary>
/// The daemon's process exit codes. Named because launchd reads them: the LaunchAgent's
/// <c>KeepAlive</c> is <c>{ Crashed: true, SuccessfulExit: false }</c>, so a NON-ZERO exit is what asks
/// launchd to try again.
/// </summary>
public static class DaemonExitCodes
{
    /// <summary>F55: another daemon holds this data root. Not a crash — the guard working.</summary>
    public const int AlreadyRunning = 3;

    /// <summary>
    /// The same refusal, reported to a supervisor that would otherwise respawn us. Zero, because
    /// launchd's <c>SuccessfulExit:false</c> means "restart while the exit status is non-zero" and there
    /// is nothing to retry: a daemon IS running against this data root, which is the state the job
    /// exists to maintain.
    /// </summary>
    public const int AlreadyRunningUnderSupervisor = 0;

    /// <summary>
    /// Set by the LaunchAgent plist (mirrored as <c>MacDaemonLaunchAgent.SupervisorVariable</c>, which
    /// <c>Mainguard.Agents</c> spells on its own side of the assembly boundary — a test pins the two
    /// equal). Its presence means something is watching this process and will restart it.
    /// </summary>
    public const string SupervisorVariable = "MAINGUARD_SUPERVISOR";

    /// <summary>
    /// N6: the exit code for "another daemon already holds this data root".
    ///
    /// <para>Interactively it stays <see cref="AlreadyRunning"/> — a human or a script running
    /// <c>mainguardd</c> must not be told a refusal succeeded. Under a supervisor it is
    /// <see cref="AlreadyRunningUnderSupervisor"/>: with the old code a developer daemon on the data root
    /// meant launchd exec'd the login job, watched it refuse with 3, and did it again every 30 seconds
    /// for as long as the developer's daemon ran. The condition resolves when a human stops the other
    /// daemon, and the app's own <c>EnsureStartedAsync</c> starts one the moment it finds no lock, so
    /// staying down costs nothing and the loop costs a process every half minute.</para>
    /// </summary>
    public static int RefusedStart() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SupervisorVariable))
            ? AlreadyRunning
            : AlreadyRunningUnderSupervisor;
}

// Exposed so WebApplicationFactory<Program> can host the daemon in-proc (TI-P2-00).
public partial class Program { }
