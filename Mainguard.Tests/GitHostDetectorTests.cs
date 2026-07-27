using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

// Regression for audit 1.7: multi-host credential detection. Tokens are keyed by
// host and the right username convention is chosen per provider (and, crucially,
// tokens are fed via git's credential mechanism, not embedded in the URL/argv).
public class GitHostDetectorTests
{
    [Theory]
    [InlineData("https://github.com/acme/repo.git", "github.com", HostKind.GitHub)]
    [InlineData("git@github.com:acme/repo.git", "github.com", HostKind.GitHub)]
    [InlineData("https://gitlab.com/acme/repo.git", "gitlab.com", HostKind.GitLab)]
    [InlineData("git@gitlab.com:acme/repo.git", "gitlab.com", HostKind.GitLab)]
    [InlineData("https://bitbucket.org/acme/repo.git", "bitbucket.org", HostKind.Bitbucket)]
    [InlineData("https://dev.azure.com/acme/proj/_git/repo", "dev.azure.com", HostKind.AzureDevOps)]
    [InlineData("https://git.internal.corp/acme/repo.git", "git.internal.corp", HostKind.Unknown)]
    public void Detect_IdentifiesHostAndKind(string url, string expectedHost, HostKind expectedKind)
    {
        var (host, kind) = GitHostDetector.Detect(url);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedKind, kind);
    }

    /// <summary>
    /// MG-40: the Azure arms matched by substring, so any host an attacker can register that merely
    /// CONTAINS the domain — <c>dev.azure.com.evil.net</c>, <c>notvisualstudio.com</c> — classified as
    /// Azure DevOps. That hands a hostile host the Azure token-username convention and, through
    /// <c>EgressAllowlistEntry.LooksLikeGitHost</c> (which asks this method), a git-host verdict on the
    /// agent egress path. Real subdomains must keep matching: SSH remotes use <c>ssh.dev.azure.com</c>
    /// and the legacy host is <c>&lt;org&gt;.visualstudio.com</c>.
    /// </summary>
    [Theory]
    [InlineData("https://dev.azure.com/acme/proj/_git/repo", HostKind.AzureDevOps)]
    [InlineData("git@ssh.dev.azure.com:v3/acme/proj/repo", HostKind.AzureDevOps)]
    [InlineData("https://acme.visualstudio.com/proj/_git/repo", HostKind.AzureDevOps)]
    [InlineData("https://dev.azure.com.evil.net/acme/repo.git", HostKind.Unknown)]
    [InlineData("git@dev.azure.com.evil.net:acme/repo.git", HostKind.Unknown)]
    [InlineData("https://notvisualstudio.com/acme/repo.git", HostKind.Unknown)]
    [InlineData("https://evil.net/dev.azure.com/repo.git", HostKind.Unknown)]
    public void Detect_MatchesAzureHostsOnTheDotBoundary_NotBySubstring(string url, HostKind expectedKind)
    {
        var (_, kind) = GitHostDetector.Detect(url);
        Assert.Equal(expectedKind, kind);
    }

    [Theory]
    [InlineData(HostKind.GitHub, "x-access-token")]
    [InlineData(HostKind.GitLab, "oauth2")]
    [InlineData(HostKind.Bitbucket, "x-token-auth")]
    [InlineData(HostKind.AzureDevOps, "token")]
    public void UsernameForToken_MatchesHostConvention(HostKind kind, string expected)
    {
        Assert.Equal(expected, GitHostDetector.UsernameForToken(kind));
    }

    [Theory]
    [InlineData(@"C:\repo")]
    [InlineData("C:/repo")]
    [InlineData(@"D:\work\project")]
    [InlineData(@"\\server\share\repo")]
    public void Detect_DoesNotMisclassifyLocalPathAsRemote(string path)
    {
        // A Windows drive / UNC path must not be read as an scp-like remote host.
        var (host, kind) = GitHostDetector.Detect(path);
        Assert.NotEqual("c", host.ToLowerInvariant());
        Assert.Equal(HostKind.Unknown, kind);
    }

    [Theory]
    // SSH forms are rewritten to HTTPS so a token can authenticate an SSH-cloned repo.
    [InlineData("git@github.com:acme/repo.git", "https://github.com/acme/repo.git")]
    [InlineData("ssh://git@github.com/acme/repo.git", "https://github.com/acme/repo.git")]
    [InlineData("ssh://git@github.com:22/acme/repo.git", "https://github.com/acme/repo.git")]
    [InlineData("git@gitlab.com:acme/repo.git", "https://gitlab.com/acme/repo.git")]
    // Already-HTTPS (and http) are returned unchanged.
    [InlineData("https://github.com/acme/repo.git", "https://github.com/acme/repo.git")]
    public void ToHttpsUrl_ConvertsSshFormsAndPreservesHttps(string url, string expected)
    {
        Assert.Equal(expected, GitHostDetector.ToHttpsUrl(url));
    }

    [Theory]
    // Local paths and empty input are not remotes — no rewrite.
    [InlineData(@"C:\repo")]
    [InlineData("C:/repo")]
    [InlineData("/home/user/repo")]
    [InlineData("")]
    public void ToHttpsUrl_ReturnsNullForNonRemotes(string url)
    {
        Assert.Null(GitHostDetector.ToHttpsUrl(url));
    }

    [Fact]
    public void TokenKeyForHost_IsFileSystemSafe()
    {
        // ':' would be an invalid filename on Windows (the keyring is file-backed).
        var key = GitHostDetector.TokenKeyForHost("github.com");
        Assert.DoesNotContain(':', key);
        Assert.Equal("token_github.com", key);
    }

    [Theory]
    [InlineData("GitHub.COM", "token_github.com")]                 // case-insensitive
    [InlineData("git.internal.corp:8443", "token_git.internal.corp_8443")] // port ':' sanitized
    public void TokenKeyForHost_NormalizesAndSanitizes(string host, string expected)
    {
        var key = GitHostDetector.TokenKeyForHost(host);
        Assert.Equal(expected, key);
        Assert.DoesNotContain(':', key);
    }
}
