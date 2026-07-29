using System;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Git.Exceptions;

/// <summary>
/// The per-repo verification toolchain a repository declared could not be provisioned into the jail.
///
/// <para><b>Why this is an exception and not a fallback.</b> Verification is the gate that decides
/// whether an agent's work may enter the merge queue. A repo that declares <c>dotnet-10</c> and gets a
/// jail without it does not have a weaker signal — it has <i>no</i> signal, and the failure mode that
/// matters is the quiet one: the command runs, the interpreter is missing, the shell exits 127, and
/// the queue records an ordinary failed verification that reads like the agent's code is broken. Worse
/// still would be falling through to "no verification command configured", which is the shape of
/// "nothing to check" rather than "the check could not be run". Both are indistinguishable from a real
/// verdict at the point a human reads the queue, so provisioning failure is raised, typed, named, and
/// never caught by the spawn path's graceful-degradation branches.</para>
/// </summary>
public class ToolchainProvisioningException : MainguardException
{
    public ToolchainProvisioningException(string repoHandle, IReadOnlyList<string> toolchainIds, string detail)
        : base($"Could not provision the verification toolchain [{string.Join(", ", toolchainIds)}] "
               + $"declared by repo '{repoHandle}': {detail}")
    {
        RepoHandle = repoHandle;
        ToolchainIds = toolchainIds;
        Detail = detail;
    }

    /// <summary>The repo whose <c>.mainguard/toolchain</c> could not be satisfied.</summary>
    public string RepoHandle { get; }

    /// <summary>The declared toolchain ids, in the order they were declared.</summary>
    public IReadOnlyList<string> ToolchainIds { get; }

    /// <summary>What actually went wrong (a docker build tail, an unknown id, a missing base digest).</summary>
    public string Detail { get; }
}

/// <summary>
/// A repository declared a toolchain id that is not in the product-owned catalog.
///
/// <para>This is deliberately a hard, typed failure rather than a skip. The catalog is a <b>closed
/// set</b>: a declaration selects a curated, pinned recipe, it never describes one. Treating an
/// unrecognised id as "install nothing" would turn a typo into a silently toolchain-less jail, and
/// treating it as "pass it through to the package manager" would turn the declaration file back into
/// the arbitrary-install surface the closed catalog exists to remove.</para>
/// </summary>
public sealed class UnknownToolchainException : ToolchainProvisioningException
{
    public UnknownToolchainException(string repoHandle, string unknownId, IEnumerable<string> knownIds)
        : base(repoHandle, new[] { unknownId },
               $"'{unknownId}' is not a Mainguard toolchain. Known toolchains: "
               + string.Join(", ", knownIds.OrderBy(id => id, StringComparer.Ordinal)) + ".")
        => UnknownId = unknownId;

    /// <summary>The id that is not in the catalog.</summary>
    public string UnknownId { get; }
}
