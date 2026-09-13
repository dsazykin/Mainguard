using System;
using System.IO;
using Mainguard.Git;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The gate every path the elevated helper WRITES TO crosses before it is opened with administrator
/// rights — the write-side twin of <see cref="TrustedExecutablePath"/>.
///
/// <para><b>What went wrong (audit F58).</b> The helper validated <c>--resume-target</c> and did not
/// validate <c>--result</c>. Both arrive on the same argv, across the same UAC boundary, and the
/// helper opens the second one with <see cref="File.WriteAllText(string,string)"/> while elevated. A
/// same-user process that persuades the user through the UAC prompt therefore got an administrator-level
/// create-or-overwrite at <b>any</b> path on the machine, with partially attacker-influenced content
/// (the JSON carries a caller-supplied <c>--resume-target</c> string inside its error field). Writing
/// over a service binary, a scheduled-task XML, or a startup script is a full escalation; even a
/// truncate-to-JSON of an arbitrary file is a denial of service no unelevated caller could achieve.</para>
///
/// <para><b>What this establishes.</b> Three independent conditions, all of which must hold:</para>
/// <list type="number">
///   <item>The path is syntactically safe — absolute, already canonical, no <c>..</c> traversal, no UNC
///   or device form, no NTFS alternate data stream, no quoting/wildcard metacharacters, no trailing
///   dot/space spellings. This is reused verbatim from <see cref="TrustedExecutablePath"/> rather than
///   reimplemented: a second, subtly different path parser is exactly how two containment checks end up
///   disagreeing, and a containment check only has to be talked around once.</item>
///   <item>The file NAME is the one fixed name both callers pass
///   (<see cref="ResultFileName"/>). The helper does not need to write anywhere else, so accepting any
///   other name would be latitude with no purpose.</item>
///   <item>The directory is a Mainguard data root — this process's own
///   <see cref="MainguardPaths.DataRoot"/>, or a directory whose final segment is one of the two names
///   that root can have (<c>Mainguard</c> on Windows, <c>.mainguard</c> elsewhere). The second form is
///   load-bearing rather than lax: under over-the-shoulder elevation the helper runs as a DIFFERENT
///   account, so its own <c>%LocalAppData%</c> is not the one the unelevated OOBE will read the result
///   back from, and requiring containment under this process's root would break that install shape
///   while adding nothing — an attacker who could name a directory called <c>Mainguard</c> under a
///   sensitive root still cannot name a FILE there other than <see cref="ResultFileName"/>.</item>
/// </list>
///
/// <para><b>What this does NOT establish.</b> Like <see cref="TrustedExecutablePath"/> it is a check on
/// the PATH, not on what already lives there. It bounds the write to one file name inside a
/// Mainguard-shaped directory; it does not prove that directory is the caller's own. That residual is
/// small by construction — the only thing the write can destroy is a Mainguard result file — and it is
/// the price of supporting elevation by a second account.</para>
/// </summary>
public static class TrustedResultPath
{
    /// <summary>The one file name the elevated helper writes. Both production callers
    /// (<c>installer/Mainguard.Installer/Program.cs</c> and <c>ProDesktopHost</c>) build exactly
    /// <c>&lt;data root&gt;/elevated-result.json</c>, so this is a fact about the product rather than a
    /// restriction invented here.</summary>
    public const string ResultFileName = "elevated-result.json";

    /// <summary>The Windows data-root folder name (<c>%LocalAppData%\Mainguard</c>).</summary>
    private const string WindowsDataRootFolder = "Mainguard";

    /// <summary>The POSIX data-root folder name (<c>~/.mainguard</c>).</summary>
    private const string PosixDataRootFolder = ".mainguard";

    /// <summary>
    /// Validates <paramref name="candidate"/> as the elevated helper's result file. On success
    /// <paramref name="canonical"/> is the normalised form the helper should actually open; on failure
    /// <paramref name="refusal"/> says why in terms a log reader can act on.
    ///
    /// <para><paramref name="dataRoot"/> is injected so the rules are unit-testable on any OS without
    /// touching the real user's data root; production passes null and gets
    /// <see cref="MainguardPaths.DataRoot"/>. A data root that cannot be resolved is not fatal — the
    /// shape rule below still applies — because the helper must stay able to report a refusal even on a
    /// machine where its own profile is unusable.</para>
    /// </summary>
    public static bool TryValidate(
        string? candidate, string? dataRoot, out string canonical, out string refusal)
    {
        canonical = string.Empty;

        // The directory is derived from the candidate with TrustedExecutablePath's own normalisation,
        // which is what makes the TryValidate call below a full syntactic pass rather than a containment
        // check against a root we let the caller choose.
        var directory = TrustedExecutablePath.DirectoryOf(candidate);
        if (directory is null)
        {
            refusal = $"'{candidate}' is not a usable absolute file path — the elevated helper's result "
                + "file must be fully qualified and named directly, never resolved against a working "
                + "directory or reached by traversal";
            return false;
        }

        if (!TrustedExecutablePath.TryValidate(candidate, directory, out var normalized, out var why))
        {
            refusal = why;
            return false;
        }

        var separator = normalized.LastIndexOfAny(new[] { '\\', '/' });
        var fileName = separator < 0 ? normalized : normalized[(separator + 1)..];
        if (!string.Equals(fileName, ResultFileName, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"'{candidate}' does not name the helper's result file — the only file the elevated "
                + $"helper writes is '{ResultFileName}', and accepting any other name would be an "
                + "administrator-level write primitive with no purpose";
            return false;
        }

        if (!IsMainguardDataDirectory(directory, dataRoot))
        {
            refusal = $"'{candidate}' is not inside a Mainguard data root — an elevated write must land in "
                + $"'{WindowsDataRootFolder}' (Windows) or '{PosixDataRootFolder}' (elsewhere), never at "
                + "an arbitrary path the caller chose";
            return false;
        }

        canonical = normalized;
        refusal = string.Empty;
        return true;
    }

    /// <summary>The throwing form, for call sites where a refusal must abort.</summary>
    public static string Require(string? candidate, string? dataRoot = null)
    {
        if (TryValidate(candidate, dataRoot, out var canonical, out var refusal))
            return canonical;
        throw new ArgumentException($"Refusing to use this result path: {refusal}.", nameof(candidate));
    }

    private static bool IsMainguardDataDirectory(string directory, string? dataRoot)
    {
        var root = dataRoot ?? TryResolveDataRoot();
        if (root is not null && TrustedExecutablePath.IsWithinOrEqual(directory, root))
            return true;

        var separator = directory.LastIndexOfAny(new[] { '\\', '/' });
        var leaf = separator < 0 ? directory : directory[(separator + 1)..];
        return string.Equals(leaf, WindowsDataRootFolder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, PosixDataRootFolder, StringComparison.Ordinal);
    }

    /// <summary>This process's data root, or null when it cannot be resolved. Never throws: a helper
    /// that cannot resolve its own profile must still be able to REFUSE, and a throw here would turn a
    /// validation into a crash on the elevated path.</summary>
    private static string? TryResolveDataRoot()
    {
        try
        {
            return MainguardPaths.DataRoot();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
