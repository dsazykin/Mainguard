using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The one gate every executable path crosses before it is handed to a privileged Windows facility —
/// <c>schtasks /RL HIGHEST /SC ONLOGON</c>, or <c>ShellExecuteEx</c> with the <c>runas</c> verb.
///
/// <para><b>What went wrong (MG-9).</b> The elevated helper took <c>--resume-target</c> straight off
/// its own argv — while already elevated — and interpolated it into a <c>schtasks</c> registration
/// with no validation at all. Anything a caller could name became an ONLOGON Scheduled Task running
/// as <c>HIGHEST</c>, which fires <b>with no UAC prompt</b> at every logon. The launcher had the
/// mirror-image hole: it checked <see cref="File.Exists(string)"/> on the helper and then ran it as
/// administrator, and it pasted the resume target into a quoted argument string a <c>"</c> in the
/// path could break out of.</para>
///
/// <para><b>What this fixes, precisely.</b> It refuses paths that are not a fully-qualified location
/// <i>inside the running install</i>: relative paths, <c>..</c> traversal, UNC and device paths
/// (<c>\\server\share</c>, <c>\\?\</c>), NTFS alternate-data-stream forms (<c>app.exe:payload</c>),
/// argument-quoting and wildcard metacharacters, and anything outside the install root. That kills the
/// "point it at an <i>arbitrary</i> executable" class outright.</para>
///
/// <para><b>What this does NOT fix — read this before trusting it.</b> It is a check on the PATH, not
/// on the BYTES. An attacker who can write to the install directory can replace the legitimate
/// executable at its legitimate path, and every check here still passes. Do not read this class as
/// "the elevated target is trusted"; read it as "the elevated target is at least the path we
/// shipped".</para>
///
/// <para><b>What now covers that gap (MG-15).</b> Two things, from opposite directions, and neither is
/// this class. <see cref="ProtectedLocationPolicy"/> removes the write primitive: the elevated helper
/// is installed under an administrator-owned root, and a no-prompt elevated Scheduled Task is only ever
/// registered against a target in one (<see cref="ResumeTaskPolicy"/>) — so on the per-user layout the
/// replaceable binary simply has no privilege left to gain. <see cref="IPayloadSignatureVerifier"/>
/// detects the replacement on a signed build. Path validation remains the first gate for the reason it
/// always was: it is the only one that works before any of the others exist.</para>
/// </summary>
public static class TrustedExecutablePath
{
    /// <summary>Characters that must never appear in a validated path. <c>"</c> is the load-bearing one:
    /// <c>schtasks /TR</c> and the <c>runas</c> <c>Arguments</c> string are both built by interpolating
    /// the path inside quotes, so a quote in the path escapes the argument and appends attacker-chosen
    /// trailing arguments. The rest are wildcards and shell/redirection metacharacters that have no
    /// business in a concrete install path and would otherwise let one registration match many files.</summary>
    private const string ForbiddenCharacters = "\"<>|*?";

    private enum PathStyle
    {
        Posix,
        Windows,
    }

    /// <summary>
    /// Validates <paramref name="candidate"/> as an executable living under
    /// <paramref name="installRoot"/>. On success <paramref name="canonical"/> is the normalised form
    /// (the exact string that should be handed on); on failure <paramref name="refusal"/> says why, in
    /// terms a user can act on.
    ///
    /// <para>Deliberately does NOT touch the filesystem: existence is a separate question the callers
    /// that can answer it ask separately, and a purely syntactic gate is testable on every OS. Windows
    /// paths validate identically on Linux CI, which is the only way these rules get exercised at all.</para>
    /// </summary>
    public static bool TryValidate(
        string? candidate, string? installRoot, out string canonical, out string refusal)
    {
        canonical = string.Empty;

        if (!TryDetectStyle(installRoot, out var style))
        {
            refusal = $"the install root '{installRoot}' is not an absolute path, so nothing can be "
                + "checked against it";
            return false;
        }

        if (!TryNormalize(installRoot!, style, requireSegments: false, out var rootPath, out var rootRefusal))
        {
            refusal = $"the install root '{installRoot}' is not a usable path: {rootRefusal}";
            return false;
        }

        if (!TryDetectStyle(candidate, out var candidateStyle))
        {
            refusal = $"'{candidate}' is not an absolute path — an elevated target must be fully "
                + "qualified, never resolved against a working directory an attacker may control";
            return false;
        }

        if (candidateStyle != style)
        {
            refusal = $"'{candidate}' is not the same kind of path as the install root '{installRoot}'";
            return false;
        }

        if (!TryNormalize(candidate!, style, requireSegments: true, out var path, out var pathRefusal))
        {
            refusal = $"'{candidate}' is refused: {pathRefusal}";
            return false;
        }

        if (!IsUnder(path, rootPath, style))
        {
            refusal = $"'{candidate}' is outside this installation ('{installRoot}') — an elevated "
                + "target must live in the directory Mainguard was installed to";
            return false;
        }

        var normalized = path.Render(style);

        // Belt and braces: when the path style matches the HOST, let the runtime canonicalise too and
        // require it to agree. This catches host-specific normalisations the syntactic pass does not
        // model (Windows silently strips trailing dots and spaces, so 'app.exe ' and 'app.exe' name one
        // file under two spellings). A disagreement means our normalisation is not the one the OS will
        // use — which is exactly the situation where a containment check can be talked around.
        if ((style == PathStyle.Windows) == OperatingSystem.IsWindows())
        {
            string full;
            try
            {
                full = Path.GetFullPath(normalized);
            }
            catch (Exception ex)
            {
                refusal = $"'{candidate}' could not be canonicalised: {ex.Message}";
                return false;
            }

            if (!string.Equals(full, normalized, Comparison(style)))
            {
                refusal = $"'{candidate}' is not already canonical (the OS resolves it to '{full}')";
                return false;
            }
        }

        canonical = normalized;
        refusal = string.Empty;
        return true;
    }

    /// <summary>The throwing form, for call sites where a refusal must abort the operation.
    /// <paramref name="what"/> names the target in the message (e.g. "resume target").</summary>
    public static string Require(string? candidate, string? installRoot, string what)
    {
        if (TryValidate(candidate, installRoot, out var canonical, out var refusal))
            return canonical;
        throw new ArgumentException($"Refusing to use this {what}: {refusal}.", nameof(candidate));
    }

    /// <summary>
    /// Whether two path strings name the same executable, compared on their CANONICAL forms and only
    /// when both are valid under <paramref name="installRoot"/>.
    ///
    /// <para><b>This is not an identity check</b>, and the caller must not describe it as one: it
    /// answers "same path", not "same program". A different binary written to the same path compares
    /// equal here. It is still worth doing, because the previous raw case-insensitive string compare
    /// also answered "same path" and got even that wrong — <c>C:\App\.\x.exe</c>, <c>C:\App\x.exe</c>
    /// and <c>C:\App\sub\..\x.exe</c> are one file and three strings.</para>
    /// </summary>
    public static bool IsSameExecutable(string? left, string? right, string? installRoot)
        => TryValidate(left, installRoot, out var a, out _)
            && TryValidate(right, installRoot, out var b, out _)
            && string.Equals(a, b, Comparison(TryDetectStyle(installRoot, out var style) ? style : PathStyle.Posix));

    /// <summary>
    /// The directory an executable sits in, derived with THIS class's own normalisation rather than
    /// <see cref="Path.GetDirectoryName(string)"/>. That matters because <c>Path</c> is host-relative:
    /// on Linux it does not treat <c>\</c> as a separator, so it answers <c>""</c> for a perfectly
    /// well-formed Windows path — which would silently turn a containment check into a check against
    /// nothing on the Linux CI leg. Returns null when the path is not a valid executable path.
    /// </summary>
    public static string? DirectoryOf(string? executablePath)
    {
        if (!TryDetectStyle(executablePath, out var style)
            || !TryNormalize(executablePath!, style, requireSegments: true, out var path, out _))
        {
            return null;
        }

        return new NormalizedPath(path.Root, path.Segments.Take(path.Segments.Count - 1).ToList())
            .Render(style);
    }

    /// <summary>
    /// Canonicalises a DIRECTORY path with the same rules executables get (absolute, no traversal, no
    /// UNC/device form, no quoting metacharacters, no trailing-dot/space spellings) but without
    /// requiring a final segment — a drive or filesystem root is a legitimate directory.
    ///
    /// <para>Added for <see cref="ProtectedLocationPolicy"/>, which has to compare install roots rather
    /// than executables. It reuses this class's normalisation on purpose: a second, subtly different
    /// path parser is how "is it inside the protected root?" and "is it inside the install root?" end
    /// up disagreeing, and a containment check only has to be talked around once.</para>
    /// </summary>
    public static bool TryCanonicalizeDirectory(string? path, out string canonical, out string refusal)
    {
        canonical = string.Empty;

        if (!TryDetectStyle(path, out var style))
        {
            refusal = "it is not an absolute path";
            return false;
        }

        if (!TryNormalize(path!, style, requireSegments: false, out var normalized, out refusal))
            return false;

        canonical = normalized.Render(style);
        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the same directory as <paramref name="root"/> or sits
    /// below it, compared segment-wise on canonical forms.
    ///
    /// <para>Unlike <see cref="IsUnder"/> this accepts equality, because "is this path inside Program
    /// Files?" must answer yes for Program Files itself. Segment-wise is load-bearing: a
    /// <see cref="string.StartsWith(string,StringComparison)"/> check would report
    /// <c>C:\Program Files Extra</c> as being inside <c>C:\Program Files</c>, which is precisely the
    /// kind of near-miss an attacker gets to choose the name of.</para>
    /// </summary>
    public static bool IsWithinOrEqual(string? candidate, string? root)
    {
        if (!TryDetectStyle(candidate, out var candidateStyle)
            || !TryDetectStyle(root, out var rootStyle)
            || candidateStyle != rootStyle)
        {
            return false;
        }

        if (!TryNormalize(candidate!, candidateStyle, requireSegments: false, out var c, out _)
            || !TryNormalize(root!, rootStyle, requireSegments: false, out var r, out _))
        {
            return false;
        }

        var comparison = Comparison(candidateStyle);
        if (!string.Equals(c.Root, r.Root, comparison))
            return false;
        if (c.Segments.Count < r.Segments.Count)
            return false;

        for (var i = 0; i < r.Segments.Count; i++)
        {
            if (!string.Equals(c.Segments[i], r.Segments[i], comparison))
                return false;
        }

        return true;
    }

    private static StringComparison Comparison(PathStyle style)
        => style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Windows when the path starts <c>X:\</c> or <c>X:/</c>, POSIX when it starts <c>/</c>.
    /// Anything else — a bare name, a relative path, a URI — is not absolute and has no style.</summary>
    private static bool TryDetectStyle(string? path, out PathStyle style)
    {
        style = PathStyle.Posix;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var p = path.Trim();
        if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/'))
        {
            style = PathStyle.Windows;
            return true;
        }

        if (p[0] == '/')
        {
            style = PathStyle.Posix;
            return true;
        }

        return false;
    }

    /// <summary>A validated absolute path split into its root and its segments.</summary>
    private readonly record struct NormalizedPath(string Root, IReadOnlyList<string> Segments)
    {
        public string Render(PathStyle style)
        {
            var separator = style == PathStyle.Windows ? '\\' : '/';
            return Segments.Count == 0
                ? Root + separator
                : Root + separator + string.Join(separator, Segments);
        }
    }

    private static bool TryNormalize(
        string raw, PathStyle style, bool requireSegments, out NormalizedPath path, out string refusal)
    {
        path = default;
        var text = raw;

        // Not trimmed for the caller: Windows silently strips trailing spaces and dots when it opens a
        // file, so ' x.exe ' and 'x.exe' would name one file under several spellings — and only one of
        // them would survive a later comparison. Refuse the sloppy spelling instead of quietly picking
        // one, so the canonical form this returns is the ONLY form that was ever accepted.
        if (text != text.Trim())
        {
            refusal = "it starts or ends with whitespace, which Windows silently strips (one file "
                + "would then have several spellings)";
            return false;
        }

        if (text.Any(char.IsControl))
        {
            refusal = "it contains control characters";
            return false;
        }

        if (text.Any(c => ForbiddenCharacters.Contains(c, StringComparison.Ordinal)))
        {
            refusal = $"it contains one of {ForbiddenCharacters} — those break the quoted argument "
                + "strings schtasks and runas are built from, or match more than one file";
            return false;
        }

        string root;
        string rest;
        if (style == PathStyle.Windows)
        {
            // A leading double separator is UNC (\\server\share) or a device path (\\?\, \\.\). Both
            // reach storage we do not control and both dodge the containment check below.
            if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
            {
                refusal = "UNC and device paths are not allowed — an elevated target must be local to "
                    + "this machine's install directory";
                return false;
            }

            root = char.ToUpperInvariant(text[0]) + ":";
            rest = text[3..];

            // Any further ':' is an NTFS alternate data stream ("app.exe:hidden.exe"): a second stream
            // on a file whose visible content looks untouched, and a form CreateProcess will execute.
            if (rest.Contains(':', StringComparison.Ordinal))
            {
                refusal = "it names an NTFS alternate data stream (a ':' after the drive letter)";
                return false;
            }
        }
        else
        {
            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                refusal = "a path beginning '//' is implementation-defined and is not allowed";
                return false;
            }

            root = string.Empty;
            rest = text[1..];

            // Legal in a POSIX filename, but these are OUR install paths: a backslash or colon here is
            // either a mistake or an attempt to read differently on a Windows consumer downstream.
            if (rest.Contains('\\', StringComparison.Ordinal) || rest.Contains(':', StringComparison.Ordinal))
            {
                refusal = "it contains a backslash or colon, which Mainguard install paths never do";
                return false;
            }
        }

        var segments = rest
            .Split(style == PathStyle.Windows ? new[] { '\\', '/' } : new[] { '/' })
            .ToList();

        // A trailing separator leaves an empty final segment: that names a DIRECTORY, not an executable.
        while (segments.Count > 0 && segments[^1].Length == 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                refusal = "it contains an empty path segment (a doubled separator)";
                return false;
            }

            if (segment is "." or "..")
            {
                // Resolved rather than rejected, '..' would let a path that LOOKS like it is inside the
                // install root walk out of it. We refuse instead of resolving: a legitimate install
                // path never contains one, so resolving buys nothing and adds a way to be wrong.
                refusal = "it contains a '.' or '..' segment — an elevated target must be named "
                    + "directly, not reached by traversal";
                return false;
            }

            // Windows strips trailing dots and spaces when opening a file, so 'x.exe.' and 'x.exe '
            // both open 'x.exe'. Allowing them would mean one file with several spellings, only one of
            // which a later comparison would recognise.
            if (segment[^1] is '.' or ' ' || segment[0] == ' ')
            {
                refusal = "a path segment starts or ends with a space or a dot, which Windows silently "
                    + "strips (one file would then have several spellings)";
                return false;
            }
        }

        if (requireSegments && segments.Count == 0)
        {
            refusal = "it names a drive or filesystem root, not an executable";
            return false;
        }

        path = new NormalizedPath(root, segments);
        refusal = string.Empty;
        return true;
    }

    /// <summary>Strict containment: the candidate must be BELOW the root, never the root itself
    /// (a directory cannot be launched) and never a sibling whose name merely shares a prefix —
    /// segment-wise comparison is what makes <c>C:\App2\x.exe</c> fail against root <c>C:\App</c>,
    /// which a naive <c>StartsWith</c> would pass.</summary>
    private static bool IsUnder(NormalizedPath candidate, NormalizedPath root, PathStyle style)
    {
        var comparison = Comparison(style);
        if (!string.Equals(candidate.Root, root.Root, comparison))
            return false;
        if (candidate.Segments.Count <= root.Segments.Count)
            return false;

        for (var i = 0; i < root.Segments.Count; i++)
        {
            if (!string.Equals(candidate.Segments[i], root.Segments[i], comparison))
                return false;
        }

        return true;
    }
}
