using System;
using System.IO;
using Microsoft.AspNetCore.DataProtection;

namespace Mainguard.Git.Security;

public interface ISecureKeyring
{
    void SaveSecret(string key, string secret);
    string? RetrieveSecret(string key);
    void DeleteSecret(string key);
}

public class SecureKeyring : ISecureKeyring, ISecureKeyStore
{
    // ISecureKeyStore (P2-01): thin delegates onto the existing storage path — no second code path.
    void ISecureKeyStore.Set(string key, string secret) => SaveSecret(key, secret);
    string? ISecureKeyStore.Get(string key) => RetrieveSecret(key);
    void ISecureKeyStore.Delete(string key) => DeleteSecret(key);
    System.Collections.Generic.IReadOnlyList<string> ISecureKeyStore.List(string prefix) => ListSecrets(prefix);

    private readonly IDataProtector _protector;
    private readonly string _storageDirectory;

    // MainguardPaths (not GetFolderPath directly): the default-option GetFolderPath returns "" on Unix
    // when the target dir doesn't exist, which turned this into the relative "Mainguard/Keyring" and
    // crash-looped mainguardd under systemd (CWD "/"). MainguardPaths always yields an absolute path or
    // throws with the remedy named.
    public SecureKeyring()
        : this(Path.Combine(MainguardPaths.DataRoot(), "Keyring"))
    {
    }

    /// <summary>
    /// Storage-directory override for testability (TI-14 #4): points the file-backed
    /// keyring at an arbitrary directory (e.g. a temp dir) so round-trip and
    /// corrupt-payload tests never touch the real user keyring.
    /// </summary>
    public SecureKeyring(string storageDirectory)
    {
        _storageDirectory = storageDirectory;
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }

        var dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storageDirectory),
            options =>
            {
                options.SetApplicationName("Mainguard");
                // Without this the master key sits in plain XML next to the secrets it
                // encrypts, so the .keyring files are only as safe as the directory ACL.
                // DPAPI (CurrentUser) keeps the key ring unreadable to other accounts.
                // Pre-existing unencrypted key files still load, so stored secrets survive
                // the upgrade; only newly generated keys gain the protection.
                if (OperatingSystem.IsWindows())
                {
                    options.ProtectKeysWithDpapi();
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // The macOS analogue: the key ring is wrapped with a master key that lives
                    // only in the login Keychain (see MacKeychainKeyProtection — fail-open to
                    // the previous plaintext posture when the Keychain is unavailable).
                    Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions
                        .Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(
                            options.Services,
                            o => o.XmlEncryptor = new MacKeychainXmlEncryptor());
                }
            }
        );
        _protector = dataProtectionProvider.CreateProtector("Mainguard.Keyring.v1");
    }

    public void SaveSecret(string key, string secret)
    {
        string encryptedSecret = _protector.Protect(secret);
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        File.WriteAllText(filePath, encryptedSecret);
    }

    public string? RetrieveSecret(string key)
    {
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string encryptedSecret = File.ReadAllText(filePath);
            return _protector.Unprotect(encryptedSecret);
        }
        catch
        {
            // Decryption failed (e.g. moved to a different machine, key revoked)
            return null;
        }
    }

    public void DeleteSecret(string key)
    {
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <summary>Stored key names (never values) with the given prefix, from the .keyring files.</summary>
    public System.Collections.Generic.IReadOnlyList<string> ListSecrets(string prefix)
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.GetFiles(_storageDirectory, "*.keyring"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
