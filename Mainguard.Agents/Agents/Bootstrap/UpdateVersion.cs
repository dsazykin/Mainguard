using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// SemVer 2.0.0 <b>precedence</b> — the one ordering every "should this update apply?" decision in the
/// app is allowed to use.
///
/// <para><b>Why this type exists (MG-14/MG-15).</b> Both update paths used to decide with
/// <c>string.Equals</c>: "the advertised version is not the string I have installed" meant "install it".
/// A string comparison has no direction, so a registry (or a MITM standing in for one) that moves its
/// <c>latest</c> tag BACKWARDS — or an app rolled back to an older build — silently DOWNGRADED the
/// component, re-introducing every vulnerability the newer release fixed. Downgrade is the cheapest
/// attack against an updater that has no signature to check, and it is the one defect on these paths
/// that can be closed without a signing identity. Ordering the versions is that fix: an update applies
/// only when it moves strictly FORWARD.</para>
///
/// <para><b>Precedence rules</b> (SemVer 2.0.0 §11), with two deliberate leniencies for the versions
/// this codebase actually sees:</para>
/// <list type="bullet">
///   <item>Build metadata (everything after the first <c>+</c>) is IGNORED for ordering, exactly as the
///   spec requires. <c>0.2.0+abc123</c> and <c>0.2.0+def456</c> are precedence-EQUAL — which is why
///   <see cref="DaemonUpdatePolicy"/> keeps its separate commit-hash rule for same-version dev
///   rebuilds: that decision is "different bytes", not "newer".</item>
///   <item>A prerelease sorts BEFORE its release (<c>1.0.0-rc.1</c> &lt; <c>1.0.0</c>); prerelease
///   identifiers compare numerically when all-digits, otherwise ordinally, numeric &lt; alphanumeric,
///   and a longer identifier list wins when every shared identifier is equal.</item>
///   <item><i>Leniency 1:</i> a core with fewer than three components (<c>1</c>, <c>1.2</c>) is
///   zero-extended, because informational versions in the wild are not always three-part.</item>
///   <item><i>Leniency 2:</i> a FOURTH numeric component is accepted and ordered (<c>1.2.3.4</c>), because
///   .NET assembly versions carry a revision. Anything else fails to parse.</item>
/// </list>
///
/// <para><b>Unparseable is not "equal" and not "newer".</b> <see cref="TryCompare"/> returns
/// <c>null</c> rather than guessing, and every caller treats that as a REFUSAL to move. Guessing here
/// would hand an attacker the downgrade back: a version string crafted to be unparseable would fall
/// through to whatever the fallback was.</para>
/// </summary>
public readonly struct UpdateVersion : IEquatable<UpdateVersion>, IComparable<UpdateVersion>
{
    /// <summary>Major/minor/patch/revision, zero-extended. Four slots so a .NET four-part assembly
    /// version orders correctly against a three-part SemVer (<c>1.2.3</c> == <c>1.2.3.0</c>).</summary>
    private readonly ulong _c0, _c1, _c2, _c3;

    /// <summary>The dot-separated prerelease identifiers, or an empty array for a release build.
    /// Never null once parsed (the default struct value is not reachable through <see cref="TryParse"/>).</summary>
    private readonly string[]? _prerelease;

    private UpdateVersion(ulong c0, ulong c1, ulong c2, ulong c3, string[] prerelease)
    {
        _c0 = c0;
        _c1 = c1;
        _c2 = c2;
        _c3 = c3;
        _prerelease = prerelease;
    }

    /// <summary>True when this version carries a prerelease tag (and therefore sorts before its release).</summary>
    public bool IsPrerelease => _prerelease is { Length: > 0 };

    /// <summary>
    /// Parses a version for PRECEDENCE. Accepts <c>&lt;core&gt;[-&lt;prerelease&gt;][+&lt;build&gt;]</c>
    /// where core is one to four dot-separated non-negative integers. Returns false — never a
    /// best-effort guess — for anything else, including an empty string, a range (<c>^1.2.3</c>), a
    /// tag (<c>latest</c>), or a core component that is not a plain integer.
    /// </summary>
    public static bool TryParse(string? raw, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();

        // Build metadata is not part of precedence — drop it before anything else looks at the string.
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];
        if (text.Length == 0)
            return false;

        // The core cannot contain '-', so the FIRST '-' starts the prerelease.
        string[] prerelease = Array.Empty<string>();
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            var tail = text[(dash + 1)..];
            text = text[..dash];
            if (tail.Length == 0)
                return false; // "1.0.0-" — a prerelease marker with no identifiers is malformed
            prerelease = tail.Split('.');
            // Identifiers are alphanumerics and hyphens; an empty one ("1.0.0-a..b") is malformed.
            if (prerelease.Any(id => id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
            return false;

        Span<ulong> core = stackalloc ulong[4];
        for (var i = 0; i < parts.Length; i++)
        {
            // NumberStyles.None: no sign, no whitespace, no thousands separators — "+1" and " 1" are
            // NOT version components, and accepting them would let two spellings of one version
            // compare unequal.
            if (!ulong.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out core[i]))
                return false;
        }

        version = new UpdateVersion(core[0], core[1], core[2], core[3], prerelease);
        return true;
    }

    /// <summary>
    /// Orders two version strings: negative when <paramref name="left"/> is older, 0 when they are
    /// precedence-equal (build metadata ignored), positive when newer — and <c>null</c> when EITHER
    /// side does not parse. Callers must treat <c>null</c> as "cannot establish that this moves
    /// forward" and refuse, never as equality.
    /// </summary>
    public static int? TryCompare(string? left, string? right)
        => TryParse(left, out var a) && TryParse(right, out var b) ? a.CompareTo(b) : null;

    /// <summary>
    /// True only when <paramref name="candidate"/> is strictly newer than <paramref name="installed"/>.
    /// An equal version, an older version, or an unparseable version on either side all answer false —
    /// this is the single predicate the update paths gate on, so "we could not tell" and "it went
    /// backwards" are both non-events rather than an install.
    /// </summary>
    public static bool IsUpgrade(string? candidate, string? installed)
        => TryCompare(candidate, installed) is > 0;

    /// <summary>
    /// True when <paramref name="candidate"/> is strictly OLDER than <paramref name="installed"/> —
    /// i.e. applying it would be a downgrade. Distinct from <c>!IsUpgrade</c> on purpose: a caller
    /// needs to tell "already current" (silent no-op) from "the source is trying to move us backwards"
    /// (must be surfaced, because it is either a rollback nobody asked for or an attack).
    /// </summary>
    public static bool IsDowngrade(string? candidate, string? installed)
        => TryCompare(candidate, installed) is < 0;

    public int CompareTo(UpdateVersion other)
    {
        if (_c0 != other._c0) return _c0.CompareTo(other._c0);
        if (_c1 != other._c1) return _c1.CompareTo(other._c1);
        if (_c2 != other._c2) return _c2.CompareTo(other._c2);
        if (_c3 != other._c3) return _c3.CompareTo(other._c3);
        return ComparePrerelease(_prerelease ?? Array.Empty<string>(), other._prerelease ?? Array.Empty<string>());
    }

    /// <summary>SemVer §11.4: a prerelease is LOWER than the matching release; otherwise identifiers
    /// are compared left to right, all-digit identifiers numerically and the rest ordinally, with a
    /// numeric identifier always lower than an alphanumeric one, and a longer list winning a tie.</summary>
    private static int ComparePrerelease(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        if (a.Count == 0) return 1;  // release > prerelease
        if (b.Count == 0) return -1; // prerelease < release

        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var an = ulong.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var av);
            var bn = ulong.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bv);
            if (an && bn)
            {
                if (av != bv) return av.CompareTo(bv);
                continue;
            }

            if (an != bn) return an ? -1 : 1; // numeric identifiers rank below alphanumeric ones
            var ordinal = string.CompareOrdinal(a[i], b[i]);
            if (ordinal != 0) return Math.Sign(ordinal);
        }

        return a.Count.CompareTo(b.Count);
    }

    public bool Equals(UpdateVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is UpdateVersion other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(_c0, _c1, _c2, _c3, string.Join('.', _prerelease ?? Array.Empty<string>()));

    public static bool operator ==(UpdateVersion left, UpdateVersion right) => left.Equals(right);

    public static bool operator !=(UpdateVersion left, UpdateVersion right) => !left.Equals(right);

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
}
