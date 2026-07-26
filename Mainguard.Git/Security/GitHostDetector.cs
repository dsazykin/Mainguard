using System;
using System.Text;
using Mainguard.Git.Models;

namespace Mainguard.Git.Security;

/// <summary>
/// Detects the Git hosting provider from a remote URL and knows the username
/// convention each host expects for token authentication. Kept separate from
/// GitService so it can be unit-tested and reused by the future multi-host
/// auth / SSH key manager (Category 2.8).
/// </summary>
public static class GitHostDetector
{
    public static (string Host, HostKind Kind) Detect(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return (string.Empty, HostKind.Unknown);

        string host = "";
        // A Windows drive path (C:\repo, D:/work) also has a ':' but is NOT a
        // remote — exclude it so it isn't misread as scp-like host "C".
        bool isWindowsDrivePath = remoteUrl.Length >= 2
            && char.IsLetter(remoteUrl[0]) && remoteUrl[1] == ':'
            && (remoteUrl.Length == 2 || remoteUrl[2] == '\\' || remoteUrl[2] == '/');

        // scp-like syntax: git@host:path (no scheme, but has a ':').
        if (remoteUrl.StartsWith("git@", StringComparison.Ordinal) ||
            (!isWindowsDrivePath && !remoteUrl.Contains("://")
                && remoteUrl.Contains(':') && !remoteUrl.Contains('\\')))
        {
            var at = remoteUrl.IndexOf('@');
            var start = at >= 0 ? at + 1 : 0;
            var colon = remoteUrl.IndexOf(':', start);
            if (colon > start) host = remoteUrl.Substring(start, colon - start);
        }
        else if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }

        var lower = host.ToLowerInvariant();
        var kind = lower switch
        {
            "github.com" => HostKind.GitHub,
            "gitlab.com" => HostKind.GitLab,
            "bitbucket.org" => HostKind.Bitbucket,
            _ when IsHostOrSubdomainOf(lower, "dev.azure.com")
                || IsHostOrSubdomainOf(lower, "visualstudio.com") => HostKind.AzureDevOps,
            _ => HostKind.Unknown
        };
        return (host, kind);
    }

    /// <summary>
    /// Exact host, or a label-aligned subdomain of it — never a substring. The Azure arms used
    /// <c>Contains</c>, so an attacker-registered <c>dev.azure.com.evil.net</c> classified as Azure DevOps:
    /// it would be handed the Azure token-username convention, and (through
    /// <c>EgressAllowlistEntry.LooksLikeGitHost</c>, which asks this method) would be judged a git host on
    /// the agent egress path. Matching on the dot boundary keeps the legitimate multi-label forms —
    /// <c>ssh.dev.azure.com</c> for scp-like remotes, <c>&lt;org&gt;.visualstudio.com</c> for the legacy
    /// host — while a suffix that merely ENDS in the domain's characters no longer qualifies.
    /// </summary>
    private static bool IsHostOrSubdomainOf(string lowerHost, string domain) =>
        lowerHost.Equals(domain, StringComparison.Ordinal)
        || lowerHost.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>
    /// Converts an SSH-style remote URL to its HTTPS equivalent so token auth can
    /// be used even when a repo was cloned over SSH (and no SSH key is present).
    /// Handles scp-like (<c>git@host:owner/repo.git</c>) and <c>ssh://</c>/<c>git://</c>
    /// forms. Returns the input unchanged when it is already HTTP(S), and
    /// <c>null</c> for local paths or anything that isn't a recognizable remote.
    /// </summary>
    public static string? ToHttpsUrl(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

        if (remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            remoteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return remoteUrl;
        }

        // A Windows drive path (C:\repo, D:/work) is not a remote.
        bool isWindowsDrivePath = remoteUrl.Length >= 2
            && char.IsLetter(remoteUrl[0]) && remoteUrl[1] == ':'
            && (remoteUrl.Length == 2 || remoteUrl[2] == '\\' || remoteUrl[2] == '/');
        if (isWindowsDrivePath) return null;

        string host;
        string path;

        if (remoteUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            remoteUrl.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return null;
            host = uri.Host;
            path = uri.AbsolutePath.TrimStart('/');
        }
        else if (!remoteUrl.Contains("://") && remoteUrl.Contains(':') && !remoteUrl.Contains('\\'))
        {
            // scp-like syntax: [user@]host:owner/repo.git
            var at = remoteUrl.IndexOf('@');
            var start = at >= 0 ? at + 1 : 0;
            var colon = remoteUrl.IndexOf(':', start);
            if (colon <= start) return null;
            host = remoteUrl.Substring(start, colon - start);
            path = remoteUrl.Substring(colon + 1).TrimStart('/');
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(host)) return null;
        return $"https://{host}/{path}";
    }

    /// <summary>
    /// Parses the <c>owner</c>/<c>repo</c> slug from a remote URL (T-23), needed to address the host's
    /// PR API. Handles HTTPS, <c>ssh://</c>/<c>git://</c>, and scp-like (<c>git@host:owner/repo.git</c>)
    /// forms; strips a trailing <c>.git</c>. Multi-segment paths (e.g. GitLab subgroups) fold every
    /// segment but the last into <c>Owner</c>. Returns <c>null</c> for local paths or anything without a
    /// clear two-segment repo path.
    /// </summary>
    public static (string Owner, string Repo)? ParseOwnerRepo(string remoteUrl)
    {
        var https = ToHttpsUrl(remoteUrl);
        if (https is null || !Uri.TryCreate(https, UriKind.Absolute, out var uri)) return null;

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - 4);

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var repo = segments[^1];
        var owner = string.Join('/', segments[..^1]);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) return null;
        return (owner, repo);
    }

    /// <summary>Username each host expects when authenticating with a token.</summary>
    public static string UsernameForToken(HostKind kind) => kind switch
    {
        HostKind.GitHub => "x-access-token",
        HostKind.GitLab => "oauth2",
        HostKind.Bitbucket => "x-token-auth",
        HostKind.AzureDevOps => "token",
        _ => "x-access-token"
    };

    /// <summary>
    /// Keyring key under which a host's token is stored. The keyring is
    /// file-backed (<c>{key}.keyring</c>), so the host is lower-cased (keys are
    /// case-insensitive) and any character that isn't filesystem-safe — notably
    /// ':' in ports / IPv6 literals — is replaced, otherwise storage/retrieval
    /// would break or collide across platforms.
    /// </summary>
    public static string TokenKeyForHost(string host)
    {
        var normalized = (host ?? string.Empty).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
        return $"token_{sb}";
    }
}
