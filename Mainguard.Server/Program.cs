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
var app = DaemonHost.Build(options);
try
{
    app.Run();
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

// Exposed so WebApplicationFactory<Program> can host the daemon in-proc (TI-P2-00).
public partial class Program { }
