using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Step 2: ensure the <c>MainguardEnv</c> distro exists, importing it from the versioned tarball when
/// absent (<c>wsl --import MainguardEnv &lt;installDir&gt; &lt;tarball&gt; --version 2</c>). A failed
/// partial import is unregistered before the typed failure so a retry starts clean (edge row 4).
/// </summary>
public sealed class ImportDistroStep : IBootstrapStep
{
    private readonly IWslRunner _wsl;
    private readonly IBootstrapFileSystem _fs;
    private readonly BootstrapOptions _options;
    private readonly BuildProvenanceGate _provenance;

    /// <param name="provenance">The MG-9 build-provenance gate for <c>MainguardOS.tar.gz</c>. Null → the
    /// compiled-in policy (a logged no-op on any build that is not a stamped attested release).</param>
    public ImportDistroStep(
        IWslRunner wsl, IBootstrapFileSystem fs, BootstrapOptions options, BuildProvenanceGate? provenance = null)
    {
        _wsl = wsl;
        _fs = fs;
        _options = options;
        _provenance = provenance ?? new BuildProvenanceGate();
    }

    public string Name => "Import MainguardEnv";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
        var distros = WslRunner.ParseDistroList(result.StdOut);
        return distros.Any(d => string.Equals(d, _options.DistroName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        if (!_fs.FileExists(_options.TarballPath))
            throw new BootstrapException(Name,
                $"The Mainguard OS payload was not found at '{_options.TarballPath}'. A packaged Mainguard "
                + "install bundles it next to the app — reinstall Mainguard, or (source runs) build it with "
                + "build/mainguardos/build.sh and stage it at that path.");

        // MG-9: this tarball becomes the ENTIRE root filesystem of a VM that then runs mainguardd as
        // root, and until now nothing checked where it came from. The attestation is produced by the CI
        // run that built the tarball, so unlike a hash the app derives from the file in front of it, it
        // cannot be re-created by whoever swapped the file. Fail-closed on a stamped release build.
        var provenance = await _provenance
            .VerifyFileAsync(BuildArtifactKind.MainguardOsTarball, _options.TarballPath, ct)
            .ConfigureAwait(false);
        if (provenance.MustRefuse)
            throw new BootstrapException(Name, $"Refusing to import {_options.DistroName}: {provenance.Reason}");
        log.Report(provenance.Reason);

        log.Report($"Importing {_options.DistroName} from {_options.TarballPath}…");
        var result = await _wsl.RunAsync(
            WslCommands.Import(_options.InstallDir, _options.TarballPath), stdin: null, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Nothing half-imported: drop the partial distro before surfacing the failure so a retry
            // is clean (edge row 4). Best-effort — the import failure is the real error.
            try { await _wsl.RunAsync(WslCommands.Unregister(), stdin: null, ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }

            // wsl --import reports its reason on stdout at least as often as stderr — carry whichever
            // is non-empty, plus the likeliest remedies (this used to end at a bare exit code).
            var detail = new[] { result.StdErr, result.StdOut }
                .Select(s => s?.Trim())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "wsl.exe reported no error text.";
            throw new BootstrapException(Name,
                $"Importing {_options.DistroName} failed (wsl --import exit {result.ExitCode}): {detail} "
                + $"Common causes: not enough free disk space at '{_options.InstallDir}', or an outdated "
                + "WSL — run `wsl --update` in PowerShell and try again.");
        }

        log.Report($"{_options.DistroName} imported.");
    }
}
