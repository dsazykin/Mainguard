using System;
using System.IO;
using System.Linq;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// B2 — what happens to a key ring that already EXISTS when the protector arrives.
///
/// <para>Configuring an <c>IXmlEncryptor</c> only ever protects newly generated keys. An install
/// that ran before the protector existed keeps its plaintext <c>key-*.xml</c>, and DataProtection
/// keeps using that key as the default until it expires 90 days later — so an operator who sets
/// <see cref="SecureKeyring.PassphraseVariable"/> on an upgraded box got no protection for the
/// secrets already stored (the audit master key among them) and no warning either. The Linux
/// protector was, for every install that mattered, prose.</para>
///
/// <para>These run on every platform because <see cref="SecureKeyring"/>'s protection can be forced
/// through its internal test seam. The unprotected posture is only reachable end to end on
/// Linux/WSL, which is exactly why its consequences went unnoticed for so long.</para>
/// </summary>
public sealed class SecureKeyringMigrationTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mainguard-keyring-mig-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
    }

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

    private static string[] KeyFiles(string dir) => Directory.GetFiles(dir, "key-*.xml");

    private static bool AnyKeyIsPlaintext(string dir) =>
        KeyFiles(dir).Any(f => File.ReadAllText(f).Contains("requiresEncryption", StringComparison.Ordinal));

    // ---- the upgrade path: a plaintext ring is re-wrapped, not left behind ----

    [Fact]
    public void APlaintextRing_IsRewrapped_WhenAPassphraseArrives_AndTheSecretsStillRead()
    {
        using var dir = new TempDir();
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        using var noPass = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var noPassFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);

        // The pre-F53 state: no protector, so the master key is in plain XML next to the ciphertext.
        var before = new SecureKeyring(dir.Path, KeyringProtection.None);
        before.SaveSecret("token_github.com", "ghp_value");
        before.SaveSecret(AuditCrypto.KeyName, "master-key-material");
        Assert.True(AnyKeyIsPlaintext(dir.Path), "the pre-upgrade ring must really be plaintext");
        var keyIdsBefore = KeyFiles(dir.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray();

        // The operator sets the passphrase and restarts.
        using var pass = new EnvScope(SecureKeyring.PassphraseVariable, "correct horse battery staple");
        var after = new SecureKeyring(dir.Path);

        Assert.Equal(KeyringProtection.Passphrase, after.Protection);
        Assert.Equal(1, after.Ring.RewrappedKeyFiles);
        Assert.False(after.Ring.HasPlaintextKeys);
        Assert.False(AnyKeyIsPlaintext(dir.Path), "no key file may still hold the master key in the clear");
        Assert.All(KeyFiles(dir.Path), f =>
            Assert.Contains("mainguardPassphraseEncryptedKey", File.ReadAllText(f), StringComparison.Ordinal));

        // A RE-WRAP, not a rotation: same key id, so every secret written under it still decrypts.
        Assert.Equal(keyIdsBefore, KeyFiles(dir.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.Equal("ghp_value", after.RetrieveSecret("token_github.com"));
        Assert.Equal("master-key-material", after.RetrieveSecret(AuditCrypto.KeyName));

        // And it survives the process: a fresh ring object reads the same secrets back.
        Assert.Equal("master-key-material", new SecureKeyring(dir.Path).RetrieveSecret(AuditCrypto.KeyName));
    }

    [Fact]
    public void AnAlreadyProtectedRing_IsLeftAlone()
    {
        using var dir = new TempDir();
        using var pass = new EnvScope(SecureKeyring.PassphraseVariable, "the operator passphrase");

        new SecureKeyring(dir.Path).SaveSecret(AuditCrypto.KeyName, "master-key-material");
        var keyXmlBefore = KeyFiles(dir.Path).Select(File.ReadAllText).ToArray();

        var reopened = new SecureKeyring(dir.Path);

        Assert.Equal(0, reopened.Ring.RewrappedKeyFiles);
        Assert.Equal(keyXmlBefore, KeyFiles(dir.Path).Select(File.ReadAllText).ToArray());
    }

    /// <summary>Without a protector there is nothing to re-wrap TO, so the plaintext is reported
    /// rather than quietly accepted — this is the accepted-risk posture, stated out loud.</summary>
    [Fact]
    public void AnUnprotectedRing_ReportsItsPlaintextKeys_InsteadOfPassingSilently()
    {
        using var dir = new TempDir();
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        using var noPass = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var noPassFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);

        new SecureKeyring(dir.Path, KeyringProtection.None).SaveSecret("token_github.com", "ghp_value");
        var reopened = new SecureKeyring(dir.Path, KeyringProtection.None);

        Assert.True(reopened.IsUnprotected);
        Assert.True(reopened.Ring.HasPlaintextKeys);
        Assert.Equal(0, reopened.Ring.RewrappedKeyFiles);
        Assert.Contains("PLAINTEXT", reopened.DescribeProtection(), StringComparison.Ordinal);
        Assert.Contains(SecureKeyring.PassphraseVariable, reopened.DescribeProtection(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same defect: a protector is configured, but the ring on disk is STILL
    /// plaintext because the re-wrap could not run. The audit master key must not be written into it,
    /// because "protected" would be a claim about keys generated from now on and nothing else.
    /// </summary>
    [Fact]
    public void AStillPlaintextRing_RefusesTheAuditMasterKey_EvenUnderAConfiguredProtector()
    {
        using var dir = new TempDir();
        using var noAllow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, null);
        using var noPass = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var noPassFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);

        // A plaintext ring written by a pre-protector install...
        using (new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1"))
        {
            new SecureKeyring(dir.Path, KeyringProtection.None).SaveSecret("token_github.com", "ghp_value");
        }

        // ...reopened claiming DPAPI (which has no encryptor to offer off Windows, so the re-wrap
        // cannot run) is still a ring whose master key is on disk in the clear.
        var reopened = new SecureKeyring(dir.Path, OperatingSystem.IsWindows()
            ? KeyringProtection.MacKeychain // stand-in: unavailable off macOS, so likewise no re-wrap
            : KeyringProtection.Dpapi);

        if (!reopened.Ring.HasPlaintextKeys)
        {
            return; // the stand-in protector happened to be available here; nothing to assert
        }

        Assert.Throws<UnprotectedKeyringException>(
            () => reopened.SaveSecret(AuditCrypto.KeyName, "master-key-material"));
    }

    // ---- the related defect: opening a protected ring without the protector ----

    /// <summary>
    /// A passphrase-wrapped ring opened with no passphrase. DataProtection's own answer is to treat
    /// the ring as having no eligible key and generate a fresh one — unencrypted, because the
    /// encryptor it would use is the one that is missing — and that new key becomes the DEFAULT. So
    /// the operator who later restores the passphrase protects nothing, and the audit payloads
    /// written in between are orphaned. The write is refused instead.
    /// </summary>
    [Fact]
    public void APassphraseRingOpenedWithoutThePassphrase_RefusesToStoreTheAuditMasterKey()
    {
        using var dir = new TempDir();
        using (new EnvScope(SecureKeyring.PassphraseVariable, "the operator passphrase"))
        {
            new SecureKeyring(dir.Path).SaveSecret(AuditCrypto.KeyName, "master-key-material");
        }

        using var cleared = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var clearedFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1"); // even then
        var reopened = new SecureKeyring(dir.Path);

        Assert.True(reopened.Ring.MasterKeyUnavailable);
        Assert.Contains(SecureKeyring.PassphraseVariable, reopened.DescribeProtection(), StringComparison.Ordinal);

        var ex = Assert.Throws<KeyringProtectorUnavailableException>(
            () => reopened.SaveSecret(AuditCrypto.KeyName, "a replacement key"));
        Assert.Equal(AuditCrypto.KeyName, ex.Key);

        // The key files are untouched: nothing was re-minted behind the operator's back.
        Assert.All(KeyFiles(dir.Path), f =>
            Assert.Contains("mainguardPassphraseEncryptedKey", File.ReadAllText(f), StringComparison.Ordinal));

        // And the passphrase coming back restores the ORIGINAL key, not a replacement.
        using var restored = new EnvScope(SecureKeyring.PassphraseVariable, "the operator passphrase");
        Assert.Equal("master-key-material", new SecureKeyring(dir.Path).RetrieveSecret(AuditCrypto.KeyName));
    }

    // ---- the passphrase FILE source (how the shipped systemd unit supplies it) ----

    [Fact]
    public void ThePassphrase_MayComeFromAFile_SoItNeedNotSitInTheProcessEnvironment()
    {
        using var dir = new TempDir();
        var passphraseFile = Path.Combine(dir.Path, "keyring.passphrase");
        Directory.CreateDirectory(dir.Path);
        // Trailing newline on purpose: `echo … > file` writes one, and it must not change the key.
        File.WriteAllText(passphraseFile, "file-supplied passphrase\n");

        using var noDirect = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var fileVar = new EnvScope(SecureKeyring.PassphraseFileVariable, passphraseFile);

        var keyring = new SecureKeyring(Path.Combine(dir.Path, "ring"));
        Assert.Equal(KeyringProtection.Passphrase, keyring.Protection);
        keyring.SaveSecret(AuditCrypto.KeyName, "master-key-material");

        // The same passphrase, this time straight from the environment, opens the same ring.
        using var viaEnv = new EnvScope(SecureKeyring.PassphraseVariable, "file-supplied passphrase");
        using var noFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);
        Assert.Equal(
            "master-key-material",
            new SecureKeyring(Path.Combine(dir.Path, "ring")).RetrieveSecret(AuditCrypto.KeyName));
    }

    [Fact]
    public void AMissingPassphraseFile_IsTheSameAsNoPassphrase_NotACrash()
    {
        using var noDirect = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var fileVar = new EnvScope(
            SecureKeyring.PassphraseFileVariable,
            Path.Combine(Path.GetTempPath(), "mainguard-no-such-passphrase-" + Guid.NewGuid().ToString("N")));

        Assert.NotEqual(KeyringProtection.Passphrase, SecureKeyring.ResolveProtection());
    }

    // ---- the read-only audit-key path (B3) ----

    [Fact]
    public void TryOpenExisting_ReturnsNull_AndWritesNothing_WhenThereIsNoMasterKey()
    {
        using var dir = new TempDir();
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        var keyring = new SecureKeyring(dir.Path, KeyringProtection.None);

        Assert.Null(AuditCrypto.TryOpenExisting(keyring));
        Assert.False(File.Exists(Path.Combine(dir.Path, AuditCrypto.KeyName + ".keyring")),
            "the read-only path must not mint a master key");
        Assert.Empty(KeyFiles(dir.Path));
    }

    [Fact]
    public void TryOpenExisting_RoundTripsWithTheMintingConstructor()
    {
        using var dir = new TempDir();
        using var allow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, "1");
        var keyring = new SecureKeyring(dir.Path, KeyringProtection.None);

        var blob = new AuditCrypto(keyring).Encrypt("the canonical envelope");

        var readOnly = AuditCrypto.TryOpenExisting(keyring);
        Assert.NotNull(readOnly);
        Assert.Equal("the canonical envelope", readOnly!.Decrypt(blob));
    }
}
