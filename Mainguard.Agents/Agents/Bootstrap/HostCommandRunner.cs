using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The macos-host implementation of <see cref="IWslRunner"/>: it accepts the in-distro command
/// shapes the bootstrap layer builds (<c>-d MainguardEnv [-u root] -- cmd…</c>), strips the WSL
/// prefix, and runs the inner command DIRECTLY on the host — because on this substrate "the place
/// commands run" IS the host (docker CLI against the resolved engine, no VM in between). Reusing
/// the existing seam this way lets <see cref="SandboxImageProvisioner"/> and friends work
/// unchanged on macOS.
///
/// <para>Any other shape — the VM lifecycle verbs (<c>--list</c>, <c>--import</c>,
/// <c>--terminate</c>, <c>--unregister</c>) — throws <see cref="InvalidOperationException"/>:
/// there is no VM of ours to manage here, and a caller reaching for one is a composition bug
/// that must fail loudly, never emulate. The <c>-u root</c> form runs as the CURRENT user:
/// root-in-distro existed for systemctl, and nothing this substrate runs needs elevation.</para>
/// </summary>
public sealed class HostCommandRunner : IWslRunner
{
    public async Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);

        var command = StripInDistroPrefix(args)
            ?? throw new InvalidOperationException(
                $"HostCommandRunner only runs in-distro command shapes; got 'wsl {string.Join(' ', args)}'. "
                + "VM lifecycle verbs have no meaning on the macos-host substrate.");

        var psi = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{command[0]}'.");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdOut = process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new WslRunResult(process.ExitCode,
            await stdOut.ConfigureAwait(false), await stdErr.ConfigureAwait(false));
    }

    /// <summary>The inner command of an in-distro shape, or null for any other shape.</summary>
    internal static IReadOnlyList<string>? StripInDistroPrefix(IReadOnlyList<string> args)
    {
        static bool Matches(IReadOnlyList<string> args, params string[] prefix) =>
            args.Count > prefix.Length && prefix.Select((p, i) => args[i] == p).All(m => m);

        if (Matches(args, "-d", WslCommands.DistroName, "--"))
        {
            return args.Skip(3).ToArray();
        }
        if (Matches(args, "-d", WslCommands.DistroName, "-u", "root", "--"))
        {
            return args.Skip(5).ToArray();
        }
        return null;
    }
}
