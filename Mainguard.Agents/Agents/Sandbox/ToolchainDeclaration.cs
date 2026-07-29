using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The parsed contents of a repository's <c>.mainguard/toolchain</c>: the ordered, de-duplicated list
/// of catalogued toolchain ids the repo's verification command needs inside the jail.
/// </summary>
/// <param name="Ids">The declared ids, in declaration order, de-duplicated. Empty = "the base image is
/// enough", which is the state every repo is in today and must stay a first-class answer.</param>
/// <param name="Normalized">The canonical text the ids hash from — comments and blank lines stripped,
/// so a comment edit is not toolchain drift and does not rebuild an image.</param>
public sealed record ToolchainDeclaration(ImmutableArray<string> Ids, string Normalized)
{
    /// <summary>The declaration a repo with no <c>.mainguard/toolchain</c> has.</summary>
    public static ToolchainDeclaration None { get; } =
        new(ImmutableArray<string>.Empty, string.Empty);

    /// <summary>True when this repo needs nothing beyond the base image.</summary>
    public bool IsEmpty => Ids.IsDefaultOrEmpty;
}

/// <summary>
/// RT-D2 provenance resolution for the per-repo verification <b>toolchain</b>, mirroring
/// <c>VerificationCommandResolver</c> file for file — same three inputs, same precedence, same flag,
/// and it arms the same <c>ChangedTestCommandGate</c>.
///
/// <para><b>The one deliberate difference, and why.</b> The verify resolver <i>runs</i> the branch-side
/// command and flags the drift. This resolver <i>provisions</i> the <b>main-side baseline</b> and flags
/// the drift. Both obey "main is authoritative", but the word has to mean something stronger here: a
/// verify-command change only decides what runs at verification time, inside an already-hardened jail,
/// under a container-runtime exit the agent cannot forge — whereas a toolchain change decides what gets
/// <i>installed</i>, as root, in an image layer, before any verification and before any human reads the
/// diff. Flagging an installation after it has already happened is not a control. So a branch-side
/// toolchain edit never reaches the provisioner at all: it is detected, flagged, and blocks the merge,
/// and the jail is built from main's list regardless.</para>
///
/// <para>A human-owned <c>pinnedToolchain</c> (the out-of-branch analogue of the verify resolver's
/// <c>pinnedCommand</c>) overrides both sides and is never flagged.</para>
/// </summary>
public static class ToolchainDeclarationResolver
{
    /// <summary>The resolved (provisionable) declaration plus whether the branch drifted from main.</summary>
    /// <param name="Provisioned">What is actually installed into the jail — main's baseline, or the human pin.</param>
    /// <param name="Declared">What the BRANCH asked for, recorded for the reviewer so the flag can be
    /// read as "the branch wanted X, the jail has Y" rather than as a bare boolean.</param>
    /// <param name="ConfigHash">SHA-256 of the branch-side config content (RT-D2 provenance).</param>
    /// <param name="ChangedVsMain">True when the branch's declaration differs from main's baseline.</param>
    public sealed record Resolution(
        ToolchainDeclaration Provisioned,
        ToolchainDeclaration Declared,
        string ConfigHash,
        bool ChangedVsMain);

    private static readonly Regex Id = new(ToolchainCatalog.IdPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves what to provision and whether to flag.
    /// </summary>
    /// <param name="branchConfigContent">The <c>.mainguard/toolchain</c> content from the branch tree (null = absent).</param>
    /// <param name="mainConfigContent">The same file from the main baseline tree (null = absent).</param>
    /// <param name="pinnedToolchain">An out-of-branch, human-owned declaration that overrides both (null = none).</param>
    /// <param name="repoHandle">Only for the typed failure's message.</param>
    public static Resolution Resolve(
        string? branchConfigContent,
        string? mainConfigContent,
        string? pinnedToolchain = null,
        string repoHandle = "")
    {
        if (!string.IsNullOrWhiteSpace(pinnedToolchain))
        {
            var pin = Parse(pinnedToolchain!, repoHandle);
            return new Resolution(pin, pin, Sha256(pinnedToolchain!), ChangedVsMain: false);
        }

        // Main's baseline is what gets built. It is parsed (and therefore validated) unconditionally,
        // because an unparseable baseline must fail loudly rather than degrade to "install nothing" —
        // the jail would then be missing the toolchain the repo's verify command needs and every
        // verification would fail for a reason that reads like the agent's fault.
        var provisioned = mainConfigContent is null ? ToolchainDeclaration.None : Parse(mainConfigContent, repoHandle);

        // The BRANCH side is only ever compared, never provisioned — so it is normalised (to compare
        // like with like) but deliberately NOT validated against the catalog. An agent that writes
        // `toolchain: rm -rf /` must produce a flagged, blocked merge, not an exception on the daemon's
        // verification path that it could use to wedge every branch in the repo.
        var declared = branchConfigContent is null
            ? ToolchainDeclaration.None
            : ParseUnvalidated(branchConfigContent);

        var changed = !string.Equals(declared.Normalized, provisioned.Normalized, StringComparison.Ordinal);

        return new Resolution(provisioned, declared, Sha256(branchConfigContent ?? string.Empty), changed);
    }

    /// <summary>
    /// Parses and VALIDATES a declaration: every id must be catalogued
    /// (<see cref="ToolchainCatalog"/>), the count is bounded, and anything that is not a bare id is
    /// refused. Used for the side that will actually be provisioned.
    /// </summary>
    public static ToolchainDeclaration Parse(string content, string repoHandle = "")
    {
        var declaration = ParseUnvalidated(content);

        if (declaration.Ids.Length > ToolchainCatalog.MaxDeclaredToolchains)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"a repository may declare at most {ToolchainCatalog.MaxDeclaredToolchains} toolchains; this one declares {declaration.Ids.Length}.");
        }

        foreach (var id in declaration.Ids)
        {
            // Grammar first, so a line that is not even shaped like an id ("dotnet-10 && curl …",
            // "https://…", "../../etc") reports as malformed rather than as an unlucky catalog miss.
            // Both refuse; only one tells the human what is actually wrong with the file.
            if (!IsWellFormedId(id))
            {
                throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                    $"'{id}' is not a toolchain id. Each non-comment line must be a single id matching "
                    + $"{ToolchainCatalog.IdPattern} — the file selects catalogued toolchains, it does not describe installs.");
            }

            if (ToolchainCatalog.TryGet(id) is null)
            {
                throw new UnknownToolchainException(repoHandle, id, ToolchainCatalog.KnownIds);
            }
        }

        return declaration;
    }

    /// <summary>SHA-256 of a config file's content, lower-case hex — the same provenance hash
    /// <c>VerificationCommandResolver.Sha256</c> produces for the verify command.</summary>
    public static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty))).ToLowerInvariant();

    /// <summary>
    /// Splits the file into candidate ids without consulting the catalog. Comments (<c>#</c> to
    /// end-of-line) and blank lines are dropped, so an edit to either is NOT drift; everything else is
    /// kept verbatim, INCLUDING tokens that could never be valid ids — the comparison side has to see
    /// exactly what the branch wrote or a hostile line could be normalised into equality with main.
    /// </summary>
    private static ToolchainDeclaration ParseUnvalidated(string content)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in (content ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var hash = rawLine.IndexOf('#');
            var line = (hash >= 0 ? rawLine[..hash] : rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // One id per line. A line carrying anything else is kept AS ONE TOKEN rather than split,
            // so `dotnet-10 && curl evil` can never normalise to the same text as a bare `dotnet-10`.
            if (seen.Add(line))
            {
                ids.Add(line);
            }
        }

        return new ToolchainDeclaration(ids.ToImmutableArray(), string.Join("\n", ids));
    }

    /// <summary>True when <paramref name="token"/> is a syntactically valid toolchain id (grammar only —
    /// it says nothing about whether the catalog has it).</summary>
    public static bool IsWellFormedId(string? token) => token is not null && Id.IsMatch(token);
}
