using System;
using System.IO;
using System.Linq;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// F53 — the two defects in <see cref="SecureKeyring"/>, tested separately because they are
/// separate bugs that happened to share a file.
///
/// <list type="number">
///   <item><b>No protector on Linux/WSL.</b> DPAPI on Windows, the login Keychain on macOS, and
///   nothing at all elsewhere: the DataProtection master key sat in plain XML in the same directory
///   as the secrets it encrypts, so the audit master key was effectively stored in the clear. There
///   is now a passphrase-derived protector, and a refusal to write the audit key without one.</item>
///   <item><b><c>RetrieveSecret</c> swallowed everything as "no secret".</b> A tampered, truncated
///   or foreign key ring was indistinguishable from a fresh install — and the audit master key's
///   caller responds to "no secret" by minting a new one, which orphans every payload the old key
///   encrypted. Absent and unreadable are now different answers.</item>
/// </list>
///
/// <para>The env-var manipulation here is safe because this assembly runs serially
/// (<c>CollectionBehavior(DisableTestParallelization = true)</c>); every test restores what it set.</para>
/// </summary>
public sealed class SecureKeyringProtectionTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mainguard-keyring-prot-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
    }

    /// <summary>Sets an environment variable for the duration of a test and puts it back.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    // ---- defect 2: absent is not the same answer as tampered ----

    [Fact]
    public void TryRetrieve_ReportsAbsent_WhenThereIsNoEntry()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);

        Assert.Equal(KeyringEntryState.Absent, keyring.TryRetrieveSecret("token_github.com", out var secret));
        Assert.Null(secret);
    }

    [Fact]
    public void TryRetrieve_ReportsPresent_AfterARoundTrip()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("token_github.com", "ghp_value");

        Assert.Equal(KeyringEntryState.Present, keyring.TryRetrieveSecret("token_github.com", out var secret));
        Assert.Equal("ghp_value", secret);
    }

    [Fact]
    public void TryRetrieve_ReportsUnreadable_NotAbsent_WhenTheEntryIsTampered()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("token_github.com", "ghp_value");

        // Flip the stored ciphertext: the file is still THERE, it just will not decrypt.
        var path = Path.Combine(dir.Path, "token_github.com.keyring");
        File.WriteAllText(path, "tampered-" + File.ReadAllText(path));

        Assert.Equal(KeyringEntryState.Unreadable, keyring.TryRetrieveSecret("token_github.com", out var secret));
        Assert.Null(secret);
        // The whole point: this is a DIFFERENT answer from the one a fresh install gives.
        Assert.Equal(KeyringEntryState.Absent, keyring.TryRetrieveSecret("token_never_stored", out _));
    }

    /// <summary>
    /// Fail closed on the audit master key. Its caller (<see cref="AuditCrypto"/>) reacts to null by
    /// generating a new key, so returning null for a tampered ring would silently orphan every audit
    /// payload on disk and leave the daemon looking healthy.
    /// </summary>
    [Fact]
    public void RetrieveSecret_Throws_WhenTheAuditMasterKeyIsPresentButUnreadable()
    {
        using var dir = new TempDir();
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret(AuditCrypto.KeyName, Convert.ToBase64String(new byte[32]));

        var path = Path.Combine(dir.Path, AuditCrypto.KeyName + ".keyring");
        File.WriteAllText(path, "tampered-" + File.ReadAllText(path));

        var ex = Assert.Throws<KeyringUnreadableException>(() => keyring.RetrieveSecret(AuditCrypto.KeyName));
        Assert.Equal(AuditCrypto.KeyName, ex.Key);
    }

    [Fact]
    public void RetrieveSecret_StillReturnsNull_WhenTheAuditMasterKeyIsSimplyAbsent()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);

        // A fresh install must stay a fresh install: only a PRESENT-and-unreadable entry fails closed.
        Assert.Null(keyring.RetrieveSecret(AuditCrypto.KeyName));
    }

    /// <summary>
    /// A host token that will not decrypt is a degraded feature, not a fault: the app has always
    /// treated "no token" as "host not configured" and turning that into an exception would make a
    /// moved-machine key ring throw out of every <c>IsSupported</c> check. The tri-state API is how
    /// a caller that cares tells the difference.
    /// </summary>
    [Fact]
    public void RetrieveSecret_DoesNotThrow_ForANonFailClosedKey()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("token_github.com", "ghp_value");
        var path = Path.Combine(dir.Path, "token_github.com.keyring");
        File.WriteAllText(path, "tampered-" + File.ReadAllText(path));

        Assert.Null(keyring.RetrieveSecret("token_github.com"));
        Assert.Equal(KeyringEntryState.Unreadable, keyring.TryRetrieveSecret("token_github.com", out _));
    }

    // ---- defect 1: the Linux/WSL protector ----

    [Fact]
    public void Passphrase_ProducesAnEncryptedKeyRing_ThatRoundTrips()
    {
        using var dir = new TempDir();
        using var pass = new EnvScope(SecureKeyring.PassphraseVariable, "correct horse battery staple");

        var keyring = new SecureKeyring(dir.Path);
        Assert.Equal(KeyringProtection.Passphrase, keyring.Protection);
        Assert.False(keyring.IsUnprotected);

        keyring.SaveSecret(AuditCrypto.KeyName, "master-key-material");
        Assert.Equal("master-key-material", new SecureKeyring(dir.Path).RetrieveSecret(AuditCrypto.KeyName));

        // The DataProtection master key is no longer plain XML sitting beside the ciphertext.
        var keyFiles = Directory.GetFiles(dir.Path, "key-*.xml");
        Assert.NotEmpty(keyFiles);
        Assert.All(keyFiles, file =>
            Assert.Contains("mainguardPassphraseEncryptedKey", File.ReadAllText(file), StringComparison.Ordinal));
    }

    /// <summary>Without the passphrase the ring is UNREADABLE, not empty — the same distinction as
    /// defect 2, arriving through the protector rather than through a tampered file.</summary>
    [Fact]
    public void PassphraseProtectedRing_ReadsAsUnreadable_WhenThePassphraseIsMissing()
    {
        using var dir = new TempDir();
        using (new EnvScope(SecureKeyring.PassphraseVariable, "the operator passphrase"))
        {
            new SecureKeyring(dir.Path).SaveSecret("token_github.com", "ghp_value");
        }

        using var cleared = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        var reopened = new SecureKeyring(dir.Path);

        Assert.Equal(KeyringEntryState.Unreadable, reopened.TryRetrieveSecret("token_github.com", out var secret));
        Assert.Null(secret);
    }

    [Fact]
    public void WrongPassphrase_ReadsAsUnreadable_NotAsAbsent()
    {
        using var dir = new TempDir();
        using (new EnvScope(SecureKeyring.PassphraseVariable, "the right one"))
        {
            new SecureKeyring(dir.Path).SaveSecret("token_github.com", "ghp_value");
        }

        using var wrong = new EnvScope(SecureKeyring.PassphraseVariable, "the wrong one");
        var reopened = new SecureKeyring(dir.Path);

        Assert.Equal(KeyringEntryState.Unreadable, reopened.TryRetrieveSecret("token_github.com", out _));
    }

    // ---- defect 1: the refusal, when no protector is available ----

    [Fact]
    public void UnprotectedRing_RefusesTheAuditMasterKey_ButNotOrdinarySecrets()
    {
        using var cleared = new EnvScope(SecureKeyring.AllowUnprotectedVariable, null);

        Assert.True(SecureKeyring.RefusesUnprotectedWrite(KeyringProtection.None, AuditCrypto.KeyName));
        Assert.False(SecureKeyring.RefusesUnprotectedWrite(KeyringProtection.None, "token_github.com"));
    }

    [Fact]
    public void ProtectedRing_RefusesNothing()
    {
        using var cleared = new EnvScope(SecureKeyring.AllowUnprotectedVariable, null);

        foreach (var protection in new[]
                 { KeyringProtection.Dpapi, KeyringProtection.MacKeychain, KeyringProtection.Passphrase })
        {
            Assert.False(SecureKeyring.RefusesUnprotectedWrite(protection, AuditCrypto.KeyName));
        }
    }

    [Fact]
    public void ExplicitOperatorOptIn_LiftsTheRefusal()
    {
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        Assert.False(SecureKeyring.RefusesUnprotectedWrite(KeyringProtection.None, AuditCrypto.KeyName));
    }

    [Fact]
    public void FailClosedKeys_NamesTheAuditMasterKey_AndTheNameMatchesItsOwner()
    {
        // The list is a literal in Security/ so it does not depend on Audit/; this pins the two together.
        Assert.Contains(AuditCrypto.KeyName, SecureKeyring.FailClosedKeys);
    }

    /// <summary>The end-to-end refusal, on the only platform that can reach an unprotected ring.</summary>
    [RequiresUnprotectedPlatformFact]
    public void OnLinux_WithoutAPassphrase_SavingTheAuditMasterKeyThrows()
    {
        using var dir = new TempDir();
        using var noPass = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var noAllow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, null);

        var keyring = new SecureKeyring(dir.Path);
        Assert.True(keyring.IsUnprotected);
        Assert.Throws<UnprotectedKeyringException>(
            () => keyring.SaveSecret(AuditCrypto.KeyName, "master-key-material"));

        // An ordinary secret still stores — the refusal is scoped, not a keyring-wide outage.
        keyring.SaveSecret("token_github.com", "ghp_value");
        Assert.Equal("ghp_value", keyring.RetrieveSecret("token_github.com"));
    }
}

/// <summary>
/// A fact that runs only where <see cref="SecureKeyring"/> can actually end up with
/// <see cref="KeyringProtection.None"/> — i.e. not Windows (DPAPI) and not macOS (login Keychain).
/// Skips visibly rather than returning early, per the repo's discovery-time skip pattern.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresUnprotectedPlatformFactAttribute : FactAttribute
{
    public RequiresUnprotectedPlatformFactAttribute()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Skip = "This platform always has a key-ring protector (DPAPI / login Keychain), so the "
                 + "unprotected refusal cannot be reached end to end here. The decision matrix is "
                 + "covered on every platform by UnprotectedRing_RefusesTheAuditMasterKey_ButNotOrdinarySecrets.";
        }
    }
}
