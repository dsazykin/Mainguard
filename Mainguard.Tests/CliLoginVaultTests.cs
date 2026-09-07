using System;
using System.Linq;
using System.Text;
using Mainguard.Agents.UI.Services;

namespace Mainguard.Tests;

/// <summary>
/// The host-side half of the CLI login round-trip: the keyring vault format
/// (<c>cli_login_&lt;kind&gt;</c> → JSON path→base64) must survive a store/parse cycle, fold a
/// harvest into an existing vault without erasing logins the harvest didn't return, and treat any
/// corrupt value as "no saved login" (a fresh interactive login) — never a crash.
/// </summary>
public sealed class CliLoginVaultTests
{
    private static CliLoginFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content));

    [Fact]
    public void RoundTrip_HarvestedFilesComeBackByteIdentical()
    {
        var harvested = new[]
        {
            File(".claude/.credentials.json", "{\"token\":\"tok-1\"}"),
            File(".claude.json", "{\"oauthAccount\":{}}"),
        };

        var vault = CliLoginVault.MergeAndSerialize(stored: null, harvested);
        var back = CliLoginVault.Parse(vault);

        Assert.Equal(2, back.Count);
        foreach (var original in harvested)
        {
            Assert.Equal(original.Content, back.Single(f => f.Path == original.Path).Content);
        }
    }

    [Fact]
    public void Merge_HarvestedPathReplacesStoredCopy_StoredOnlyPathsSurvive()
    {
        var first = CliLoginVault.MergeAndSerialize(null, new[]
        {
            File(".gemini/oauth_creds.json", "old-token"),
            File(".gemini/settings.json", "{\"selectedAuthType\":\"oauth\"}"),
        });

        // The next session refreshed the token but never touched settings.json.
        var second = CliLoginVault.MergeAndSerialize(first, new[]
        {
            File(".gemini/oauth_creds.json", "new-token"),
        });

        var back = CliLoginVault.Parse(second);
        Assert.Equal("new-token", Encoding.UTF8.GetString(
            back.Single(f => f.Path == ".gemini/oauth_creds.json").Content));
        // A file the harvest didn't return must NOT erase the stored login half.
        Assert.Contains(back, f => f.Path == ".gemini/settings.json");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\".claude.json\": \"not-base64!!!\"}")]
    public void Parse_CorruptOrEmptyVault_YieldsEmpty_NeverThrows(string? stored)
    {
        Assert.Empty(CliLoginVault.Parse(stored));
    }

    [Fact]
    public void Parse_OneCorruptEntry_LosesThatFileNotTheVault()
    {
        var vault = "{\".claude.json\": \"not-base64!!!\", \".claude/.credentials.json\": \""
            + Convert.ToBase64String(Encoding.UTF8.GetBytes("ok")) + "\"}";

        var back = Assert.Single(CliLoginVault.Parse(vault));
        Assert.Equal(".claude/.credentials.json", back.Path);
    }

    [Fact]
    public void MergeAndSerialize_NothingToStore_ReturnsNull()
    {
        Assert.Null(CliLoginVault.MergeAndSerialize(null, Array.Empty<CliLoginFile>()));
    }

    [Fact]
    public void KeystoreKey_IsPerAdapterKind_AndPerRepo()
    {
        var key = CliLoginVault.KeystoreKeyFor("claude-code", "repo-a");

        Assert.NotNull(key);
        Assert.StartsWith("cli_login_claude-code_", key);
        // A fixed-width hex suffix, so no (kind, repo) pair can be re-parsed as another one.
        Assert.Equal("cli_login_claude-code_".Length + 32, key!.Length);
    }

    /// <summary>F2: the whole point of the key change. Two repositories must not resolve to one
    /// entry — that shared entry is what carried a login out of the repo it was made in.</summary>
    [Fact]
    public void KeystoreKey_DiffersPerRepo_ForTheSameKind()
    {
        Assert.NotEqual(
            CliLoginVault.KeystoreKeyFor("claude-code", "repo-a"),
            CliLoginVault.KeystoreKeyFor("claude-code", "repo-b"));
    }

    /// <summary>A separator that both scopes may contain must not let one pair spell another's key.</summary>
    [Fact]
    public void KeystoreKey_CannotCollideAcrossTheSeparator()
    {
        Assert.NotEqual(
            CliLoginVault.KeystoreKeyFor("a_b", "c"),
            CliLoginVault.KeystoreKeyFor("a", "b_c"));
    }

    [Fact]
    public void KeystoreKey_IsNull_WhenEitherScopeIsBlank()
    {
        Assert.Null(CliLoginVault.KeystoreKeyFor("claude-code", " "));
        Assert.Null(CliLoginVault.KeystoreKeyFor("claude-code", null));
        Assert.Null(CliLoginVault.KeystoreKeyFor("", "repo-a"));
    }

    /// <summary>The pre-F2 shape, pinned only so the migration note has something to point at. Nothing
    /// reads this entry's value any more; a read fallback to it would be the defect restored.</summary>
    [Fact]
    public void LegacyKeystoreKey_IsTheUnscopedName_AndIsNeverProducedByTheScopedOne()
    {
        Assert.Equal("cli_login_claude-code", CliLoginVault.LegacyKeystoreKeyFor("claude-code"));
        Assert.NotEqual(
            CliLoginVault.LegacyKeystoreKeyFor("claude-code"),
            CliLoginVault.KeystoreKeyFor("claude-code", "repo-a"));
    }
}
