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
    return DaemonExitCodes.AlreadyRunning;
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
    return DaemonExitCodes.AlreadyRunning;
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

    throw new DaemonStartupException(options.Port,
        $"Mainguard daemon could not bind loopback port {options.Port} (already in use?).", ex);
}

return 0;

/// <summary>
/// The daemon's process exit codes. Named because launchd reads them: the LaunchAgent's
/// <c>KeepAlive</c> is <c>{ SuccessfulExit: false }</c>, so a non-zero exit is what asks launchd to try
/// again — and <see cref="AlreadyRunning"/> deliberately IS non-zero, because a second instance losing to
/// a first is a condition that resolves itself when the first one stops.
/// </summary>
public static class DaemonExitCodes
{
    /// <summary>F55: another daemon holds this data root. Not a crash — the guard working.</summary>
    public const int AlreadyRunning = 3;
}

// Exposed so WebApplicationFactory<Program> can host the daemon in-proc (TI-P2-00).
public partial class Program { }
