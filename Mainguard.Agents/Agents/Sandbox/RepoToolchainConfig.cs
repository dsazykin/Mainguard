using System.IO;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Reads a repository's <c>.mainguard/toolchain</c> <b>out of git</b>, from the daemon-side bare
/// mirror — never off a working tree.
///
/// <para>That is not incidental. The verify command has always been read with <c>git show
/// &lt;ref&gt;:&lt;path&gt;</c> for exactly one reason: it makes the main-side baseline and the
/// branch-side value two <i>committed trees</i>, so drift between them is a fact about history rather
/// than about whatever happens to be on disk in a jail the agent can write to. The toolchain
/// declaration decides what gets installed, so it needs the stronger version of the same property: the
/// bytes the provisioner acts on come from the main branch of the mirror, which no jail can write.</para>
/// </summary>
public static class RepoToolchainConfig
{
    /// <summary>The tracked, in-tree path a repository declares its verification toolchain at.</summary>
    public const string Path = ".mainguard/toolchain";

    /// <summary>
    /// The MAIN-side baseline declaration — the one and only declaration that is ever provisioned.
    /// Parsed and validated, so a malformed or uncatalogued baseline is a typed failure here rather
    /// than a jail that silently lacks the toolchain its verify command needs.
    /// </summary>
    /// <param name="barePath">The daemon-side bare mirror for the repo.</param>
    /// <param name="repoHandle">Only for the typed failure's message.</param>
    /// <exception cref="Mainguard.Git.Exceptions.ToolchainProvisioningException">The baseline is malformed
    /// or names something the catalog does not have.</exception>
    public static ToolchainDeclaration ReadMainBaseline(string barePath, string repoHandle)
    {
        var content = Show(barePath, DefaultBranch(barePath), Path);
        return content is null ? ToolchainDeclaration.None : ToolchainDeclarationResolver.Parse(content, repoHandle);
    }

    /// <summary><c>git show &lt;reference&gt;:&lt;path&gt;</c> against the mirror; null when the ref or
    /// the path is absent (an absent declaration is the ordinary "this repo needs nothing" case).</summary>
    public static string? Show(string barePath, string reference, string path)
        => TryGit(barePath, out var output, "show", $"{reference}:{path}") ? output : null;

    /// <summary>The mirror's default branch (its symbolic HEAD), falling back to <c>main</c>.</summary>
    public static string DefaultBranch(string barePath)
    {
        if (TryGit(barePath, out var output, "symbolic-ref", "--short", "HEAD"))
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    private static bool TryGit(string barePath, out string output, params string[] args)
    {
        output = string.Empty;
        if (!Directory.Exists(barePath))
        {
            return false;
        }

        return AgentGitCommand.TryRun(barePath, out output, args) == 0;
    }
}
