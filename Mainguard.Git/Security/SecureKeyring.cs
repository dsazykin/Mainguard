using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Mainguard.Git.Security;

public interface ISecureKeyring
{
    void SaveSecret(string key, string secret);
    string? RetrieveSecret(string key);
    void DeleteSecret(string key);
}

/// <summary>What a keyring entry turned out to be when it was read (F53). The point is that
/// <see cref="Absent"/> and <see cref="Unreadable"/> are different answers: a tampered, truncated or
/// foreign key ring used to be reported as "no secret", which is indistinguishable from a fresh
/// install — and a caller that generates a new secret when there is none (the audit master key does
/// exactly that) then silently orphans every payload the old key encrypted.</summary>
public enum KeyringEntryState
{
    /// <summary>No file for this key. A fresh install, or a secret the user never configured.</summary>
    Absent,

    /// <summary>The entry was read and decrypted.</summary>
    Present,

    /// <summary>The entry EXISTS but could not be decrypted: tampered, truncated, written by another
    /// machine's key ring, or protected by a key this process cannot get at.</summary>
    Unreadable,
}

/// <summary>How the DataProtection key ring under a <see cref="SecureKeyring"/> is protected at rest.</summary>
public enum KeyringProtection
{
    /// <summary>Nothing. The key ring's master key sits in plain XML next to the secrets it
    /// encrypts, so the <c>.keyring</c> files are only as safe as the directory's permissions.</summary>
    None,

    /// <summary>Windows DPAPI, CurrentUser scope.</summary>
    Dpapi,

    /// <summary>macOS: a master key held in the login Keychain (<see cref="MacKeychainXmlEncryptor"/>).</summary>
    MacKeychain,

    /// <summary>An operator-supplied passphrase (<see cref="SecureKeyring.PassphraseVariable"/>),
    /// stretched with PBKDF2-HMAC-SHA256 into an AES-256-GCM key. The Linux/WSL leg.</summary>
    Passphrase,
}

/// <summary>Thrown when a keyring entry exists but cannot be decrypted. Fail closed: the caller must
/// not mistake a tampered ring for an empty one.</summary>
public sealed class KeyringUnreadableException : Exception
{
    public KeyringUnreadableException(string key, Exception? inner = null)
        : base($"The keyring entry '{key}' exists but could not be decrypted. The key ring may have "
               + "been tampered with, truncated, or written on another machine. Refusing to treat it "
               + "as absent — move or delete the entry deliberately if it really is unrecoverable.", inner)
        => Key = key;

    public string Key { get; }
}

/// <summary>Thrown when a secret that must not sit unprotected is written to an unprotected key ring.</summary>
public sealed class UnprotectedKeyringException : Exception
{
    public UnprotectedKeyringException(string key)
        : base($"Refusing to store '{key}' in an UNPROTECTED key ring. On this platform the "
               + "DataProtection master key would sit in plain XML next to the secrets it encrypts, "
               + $"so anything that can read the file can read the secret. Set {SecureKeyring.PassphraseVariable} "
               + "to a passphrase (the key ring is then AES-256-GCM encrypted under a PBKDF2-derived key), "
               + $"or set {SecureKeyring.AllowUnprotectedVariable}=1 to accept the plaintext posture "
               + "deliberately.")
        => Key = key;

    public string Key { get; }
}

public class SecureKeyring : ISecureKeyring, ISecureKeyStore
{
    /// <summary>
    /// The operator-supplied key-ring passphrase. When set, the DataProtection key ring is encrypted
    /// with an AES-256-GCM key derived from it (PBKDF2-HMAC-SHA256, per-key random salt recorded in
    /// the key XML) — the Linux/WSL protector. Honoured on every platform, so a headless macOS or
    /// Windows service can opt into it too.
    /// </summary>
    public const string PassphraseVariable = "MAINGUARD_KEYRING_PASSPHRASE";

    /// <summary>Set to <c>1</c>/<c>true</c> to accept an unprotected key ring for the keys that
    /// otherwise refuse to be stored in one (see <see cref="FailClosedKeys"/>).</summary>
    public const string AllowUnprotectedVariable = "MAINGUARD_ALLOW_UNPROTECTED_KEYRING";

    /// <summary>
    /// Keys that must not be written to an unprotected key ring without an explicit operator opt-in.
    ///
    /// <para>Only the audit master key is on this list, and the asymmetry is deliberate. A missing
    /// <c>token_&lt;host&gt;</c> means "the user has not configured that host", which is a normal
    /// state the app degrades through every day; making it throw would turn a moved-machine key ring
    /// into an app-wide fault. The audit master key is the opposite: it is the confidentiality of the
    /// tamper-evident chain, its absence causes a NEW key to be generated on the spot, and the store
    /// it protects is the one an investigator later has to trust.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> FailClosedKeys = new[] { "audit-payload-key" };

    // ISecureKeyStore (P2-01): thin delegates onto the existing storage path — no second code path.
    void ISecureKeyStore.Set(string key, string secret) => SaveSecret(key, secret);
    string? ISecureKeyStore.Get(string key) => RetrieveSecret(key);
    void ISecureKeyStore.Delete(string key) => DeleteSecret(key);
    IReadOnlyList<string> ISecureKeyStore.List(string prefix) => ListSecrets(prefix);

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

        Protection = ResolveProtection();

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
                switch (Protection)
                {
                    case KeyringProtection.Passphrase:
                        // F53 — the Linux/WSL leg, which previously had NO protector at all: the key
                        // ring is wrapped with an AES-256-GCM key derived from the operator's
                        // passphrase. Chosen over libsecret (needs a session D-Bus that a systemd
                        // daemon does not have, plus a native dependency), the kernel keyring (does
                        // not survive a reboot, so the audit master key would be lost with it) and
                        // systemd-creds (needs root for the host key, and WSL frequently has no
                        // systemd). This one needs no new package and works headless.
                        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions
                            .Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(
                                options.Services,
                                o => o.XmlEncryptor = new PassphraseXmlEncryptor());
                        break;

                    case KeyringProtection.Dpapi:
                        if (OperatingSystem.IsWindows())
                        {
                            options.ProtectKeysWithDpapi();
                        }

                        break;

                    case KeyringProtection.MacKeychain:
                        // The macOS analogue: the key ring is wrapped with a master key that lives
                        // only in the login Keychain (see MacKeychainKeyProtection — fail-open to
                        // the previous plaintext posture when the Keychain is unavailable).
                        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions
                            .Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(
                                options.Services,
                                o => o.XmlEncryptor = new MacKeychainXmlEncryptor());
                        break;

                    case KeyringProtection.None:
                    default:
                        // Nothing available. SaveSecret refuses the fail-closed keys rather than
                        // writing them under a plaintext master key without the operator saying so.
                        break;
                }
            }
        );
        _protector = dataProtectionProvider.CreateProtector("Mainguard.Keyring.v1");
    }

    /// <summary>How this key ring's master key is protected at rest.</summary>
    public KeyringProtection Protection { get; }

    /// <summary>True when the key ring has no at-rest protector, i.e. its master key is stored in
    /// plain XML beside the ciphertext it protects. A caller that surfaces daemon health should say
    /// so out loud rather than letting it pass unremarked.</summary>
    public bool IsUnprotected => Protection == KeyringProtection.None;

    public void SaveSecret(string key, string secret)
    {
        if (RefusesUnprotectedWrite(Protection, key))
        {
            throw new UnprotectedKeyringException(key);
        }

        string encryptedSecret = _protector.Protect(secret);
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        WriteOwnerOnly(filePath, encryptedSecret);
    }

    /// <summary>
    /// The secret, or null when there is none.
    ///
    /// <para>F53 — an entry that EXISTS but cannot be decrypted is no longer reported as "no secret".
    /// For a <see cref="FailClosedKeys"/> entry it throws <see cref="KeyringUnreadableException"/>,
    /// because the caller's response to null there is to mint a fresh key and carry on, which makes a
    /// tampered ring look exactly like a fresh install. For every other key it still returns null (a
    /// host token that will not decrypt is a degraded feature, not a fault), and callers that need to
    /// tell the two apart use <see cref="TryRetrieveSecret"/>.</para>
    /// </summary>
    public string? RetrieveSecret(string key)
    {
        var state = TryRetrieveSecret(key, out var secret);
        if (state == KeyringEntryState.Unreadable && IsFailClosedKey(key))
        {
            throw new KeyringUnreadableException(key);
        }

        return secret;
    }

    /// <summary>
    /// Reads <paramref name="key"/> and reports WHICH of the three outcomes happened. The whole
    /// point of F53: "absent" and "present but unreadable" are different facts, and every caller that
    /// would react differently to a tampered ring than to a fresh one needs to be able to see the
    /// difference.
    /// </summary>
    public KeyringEntryState TryRetrieveSecret(string key, out string? secret)
    {
        secret = null;
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        if (!File.Exists(filePath))
        {
            return KeyringEntryState.Absent;
        }

        try
        {
            string encryptedSecret = File.ReadAllText(filePath);
            secret = _protector.Unprotect(encryptedSecret);
            return KeyringEntryState.Present;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException
                                       or UnauthorizedAccessException)
        {
            // The file is THERE and would not decrypt: tampered, truncated, foreign machine, or a
            // master key this process cannot reach. Never "absent".
            return KeyringEntryState.Unreadable;
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
    public IReadOnlyList<string> ListSecrets(string prefix)
    {
        var names = new List<string>();
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

    /// <summary>
    /// The write refusal, as a pure decision: an unprotected key ring, a key that must not sit in
    /// one, and no operator opt-in. Internal so the matrix is testable on every platform — the
    /// end-to-end refusal itself only ever fires on Linux/WSL, which is precisely why it went
    /// unnoticed.
    /// </summary>
    internal static bool RefusesUnprotectedWrite(KeyringProtection protection, string key)
        => protection == KeyringProtection.None && IsFailClosedKey(key) && !AllowUnprotected();

    private static bool IsFailClosedKey(string key)
    {
        foreach (var candidate in FailClosedKeys)
        {
            if (string.Equals(candidate, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool AllowUnprotected()
    {
        var value = Environment.GetEnvironmentVariable(AllowUnprotectedVariable);
        return value is "1" or "true" or "TRUE" or "True" or "yes";
    }

    private static KeyringProtection ResolveProtection()
    {
        // The passphrase wins on every platform: an operator who sets it has said what they want,
        // and a headless service is exactly where the OS-bound protectors are unavailable.
        if (PassphraseKeyDerivation.PassphraseIsSet())
        {
            return KeyringProtection.Passphrase;
        }

        if (OperatingSystem.IsWindows())
        {
            return KeyringProtection.Dpapi;
        }

        if (OperatingSystem.IsMacOS())
        {
            return KeyringProtection.MacKeychain;
        }

        return KeyringProtection.None;
    }

    /// <summary>Writes a <c>.keyring</c> file owner-only: pre-created <c>0600</c> on Unix so the
    /// ciphertext never lands under the process umask, single-ACE DACL on Windows.</summary>
    private static void WriteOwnerOnly(string path, string content)
    {
        var exists = File.Exists(path);
        if (!exists && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(path, content);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>
/// F53 — at-rest protection for the DataProtection key ring on Linux/WSL (and anywhere else an
/// operator asks for it): the key-ring XML is AES-256-GCM encrypted under a key stretched from
/// <see cref="SecureKeyring.PassphraseVariable"/> with PBKDF2-HMAC-SHA256.
///
/// <para>The salt and iteration count are recorded IN the encrypted element, so the decryptor needs
/// nothing but the passphrase — no directory coupling, no process-static cache, and a re-encrypted
/// key ring gets a fresh salt. Unlike the macOS leg this does NOT fail open: a key written under a
/// passphrase and read without one raises, because silently falling back to plaintext would defeat
/// the point of having set the passphrase in the first place.</para>
/// </summary>
public sealed class PassphraseXmlEncryptor : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var salt = RandomNumberGenerator.GetBytes(PassphraseKeyDerivation.SaltSize);
        var master = PassphraseKeyDerivation.DeriveOrThrow(salt, PassphraseKeyDerivation.Iterations);

        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(master, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        ciphertext.CopyTo(payload, nonce.Length + tag.Length);

        return new EncryptedXmlInfo(
            new XElement("mainguardPassphraseEncryptedKey",
                new XComment($" AES-256-GCM; key stretched from {SecureKeyring.PassphraseVariable} "),
                new XElement("salt", Convert.ToBase64String(salt)),
                new XElement("iterations", PassphraseKeyDerivation.Iterations),
                new XElement("value", Convert.ToBase64String(payload))),
            typeof(PassphraseXmlDecryptor));
    }
}

/// <summary>The decryptor half — resolved by type name from the key XML.</summary>
public sealed class PassphraseXmlDecryptor : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var salt = Convert.FromBase64String(
            encryptedElement.Element("salt")?.Value
            ?? throw new CryptographicException("mainguardPassphraseEncryptedKey element carries no salt."));
        var iterations = int.TryParse(encryptedElement.Element("iterations")?.Value, out var parsed)
            ? parsed
            : PassphraseKeyDerivation.Iterations;
        var payload = Convert.FromBase64String(
            encryptedElement.Element("value")?.Value
            ?? throw new CryptographicException("mainguardPassphraseEncryptedKey element carries no value."));

        var master = PassphraseKeyDerivation.DeriveOrThrow(salt, iterations);
        var nonce = payload.AsSpan(0, 12).ToArray();
        var tag = payload.AsSpan(12, 16).ToArray();
        var ciphertext = payload.AsSpan(28).ToArray();
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(master, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return XElement.Parse(Encoding.UTF8.GetString(plaintext));
    }
}

/// <summary>PBKDF2-HMAC-SHA256 stretching of the operator passphrase into a 256-bit AES key.</summary>
internal static class PassphraseKeyDerivation
{
    internal const int SaltSize = 32;

    /// <summary>OWASP's 2023 PBKDF2-HMAC-SHA256 floor. This runs once per key-ring open, not per
    /// secret, so the cost is paid at construction and never in a hot path.</summary>
    internal const int Iterations = 600_000;

    internal static bool PassphraseIsSet()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SecureKeyring.PassphraseVariable));

    internal static byte[] DeriveOrThrow(byte[] salt, int iterations)
    {
        var passphrase = Environment.GetEnvironmentVariable(SecureKeyring.PassphraseVariable);
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new CryptographicException(
                $"This Mainguard key ring is encrypted with {SecureKeyring.PassphraseVariable}, but the "
                + "variable is not set in this process. Set it to the same passphrase and retry.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
