using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Mainguard.Git.Security;

/// <summary>
/// What the DataProtection key ring on disk turned out to be when <see cref="SecureKeyring"/> opened
/// it, and what was done about it.
///
/// <para>This exists because configuring an <c>IXmlEncryptor</c> only ever protects <b>newly
/// generated</b> keys. An install that ran before the protector existed keeps a plaintext
/// <c>key-*.xml</c>, and that key stays the DEFAULT one until it expires (90 days) — so an operator
/// who sets <see cref="SecureKeyring.PassphraseVariable"/> on an upgraded box would get no
/// protection for the secrets already stored under it, and no warning either. The ring is therefore
/// inspected at open, re-wrapped in place when a protector is available, and reported so the daemon
/// can say what it found out loud.</para>
/// </summary>
/// <param name="KeyFiles">How many <c>key-*.xml</c> files the ring holds.</param>
/// <param name="PlaintextKeyFiles">
/// How many of them still hold the master key in the clear AFTER the re-wrap attempt — zero on a
/// healthy protected ring, and equal to <paramref name="KeyFiles"/> on an unprotected one.
/// </param>
/// <param name="RewrappedKeyFiles">How many were converted from plaintext to protected at this open.</param>
/// <param name="MasterKeyUnavailable">
/// True when the ring's key files are encrypted under a protector THIS process cannot use — the
/// passphrase is not set, or the login Keychain will not serve the master key. Left unhandled,
/// DataProtection treats such a ring as having no eligible key and quietly generates a fresh one
/// (unencrypted, since the protector it would use is the one that is unavailable), which then
/// becomes the default: restoring the passphrase later would protect nothing.
/// </param>
/// <param name="Detail">Human-readable reason/outcome for the log line; null when there is nothing to say.</param>
public sealed record KeyringRingReport(
    int KeyFiles,
    int PlaintextKeyFiles,
    int RewrappedKeyFiles,
    bool MasterKeyUnavailable,
    string? Detail)
{
    internal static KeyringRingReport Empty { get; } = new(0, 0, 0, false, null);

    /// <summary>True when at least one key file still holds the master key in plain XML beside the
    /// ciphertext it protects.</summary>
    public bool HasPlaintextKeys => PlaintextKeyFiles > 0;
}

/// <summary>
/// Reads and, where it can, repairs the on-disk DataProtection key ring. Everything here operates on
/// the key XML that <c>XmlKeyManager</c> writes, in exactly the shape it writes it: an element marked
/// <c>requiresEncryption="true"</c> is the plaintext master key, and the encrypted form is that
/// element replaced by <c>&lt;encryptedSecret decryptorType="…"&gt;</c> in the DataProtection
/// namespace. Re-wrapping preserves the key id and the key material, so every <c>.keyring</c> file
/// already written under it keeps decrypting — this is a re-wrap, not a rotation.
/// </summary>
internal static class KeyringRingMaintenance
{
    private static readonly XNamespace DataProtectionNs = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName EncryptedSecret = DataProtectionNs + "encryptedSecret";
    private static readonly XName RequiresEncryption = DataProtectionNs + "requiresEncryption";

    /// <summary>The <see cref="MacKeychainXmlEncryptor"/> fail-open wrapper: "encrypted" in name only.</summary>
    private const string FailOpenWrapper = "unencryptedKey";

    private const string PassphraseWrapper = "mainguardPassphraseEncryptedKey";
    private const string KeychainWrapper = "macKeychainEncryptedKey";

    /// <summary>
    /// Inspects every <c>key-*.xml</c> under <paramref name="directory"/> and, when
    /// <paramref name="encryptor"/> can protect them, re-wraps the plaintext ones in place.
    /// </summary>
    internal static KeyringRingReport InspectAndRewrap(
        string directory, KeyringProtection protection, IXmlEncryptor? encryptor)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "key-*.xml");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KeyringRingReport.Empty with { Detail = $"key ring not readable: {ex.Message}" };
        }

        if (files.Length == 0)
        {
            return KeyringRingReport.Empty;
        }

        var plaintext = 0;
        var rewrapped = 0;
        var unavailable = false;
        var notes = new List<string>();

        foreach (var file in files)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                notes.Add($"{Path.GetFileName(file)} unreadable ({ex.GetType().Name})");
                continue;
            }

            if (WrapperIsUnusable(doc, out var reason))
            {
                unavailable = true;
                notes.Add(reason);
                continue;
            }

            if (!IsPlaintext(doc))
            {
                continue;
            }

            XDocument? rewrappedDoc = null;
            string? failure = null;
            if (encryptor is null || !TryRewrap(doc, encryptor, out rewrappedDoc, out failure))
            {
                plaintext++;
                if (failure is not null)
                {
                    notes.Add(failure);
                }

                continue;
            }

            if (TryWriteAtomically(file, rewrappedDoc, out var writeFailure))
            {
                rewrapped++;
            }
            else
            {
                plaintext++;
                notes.Add(writeFailure!);
            }
        }

        if (plaintext > 0 && protection == KeyringProtection.None)
        {
            notes.Add(
                $"{plaintext} key file(s) hold the master key in plain XML and this platform has no "
                + $"protector — set {SecureKeyring.PassphraseVariable} (or "
                + $"{SecureKeyring.PassphraseFileVariable}) to encrypt the ring at rest");
        }

        return new KeyringRingReport(
            files.Length, plaintext, rewrapped, unavailable, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>The encryptor matching a resolved protection, or null when there is none to build
    /// (an unprotected platform, or DPAPI off Windows).</summary>
    internal static IXmlEncryptor? CreateEncryptor(KeyringProtection protection) => protection switch
    {
        KeyringProtection.Passphrase => new PassphraseXmlEncryptor(),
        KeyringProtection.MacKeychain => new MacKeychainXmlEncryptor(),
        KeyringProtection.Dpapi when OperatingSystem.IsWindows() => CreateDpapiEncryptor(),
        _ => null,
    };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IXmlEncryptor CreateDpapiEncryptor()
        => new DpapiXmlEncryptor(
            protectToLocalMachine: false, loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    /// <summary>True when the ring is encrypted under a protector this process cannot use, which is
    /// the state that makes DataProtection mint a fresh (unprotected) default key behind the operator's
    /// back.</summary>
    private static bool WrapperIsUnusable(XDocument doc, out string reason)
    {
        reason = string.Empty;
        foreach (var secret in doc.Descendants(EncryptedSecret))
        {
            var wrapper = secret.Elements().FirstOrDefault()?.Name.LocalName;
            switch (wrapper)
            {
                case PassphraseWrapper when PassphraseKeyDerivation.Resolve() is null:
                    reason = $"the key ring is passphrase-encrypted but {SecureKeyring.PassphraseVariable} "
                             + $"(or {SecureKeyring.PassphraseFileVariable}) is not set in this process";
                    return true;

                case KeychainWrapper when MacKeychainMasterKey.GetOrCreate() is null:
                    reason = "the key ring is encrypted under a login-Keychain master key this process "
                             + "cannot read (locked or unavailable Keychain)";
                    return true;
            }
        }

        return false;
    }

    /// <summary>True when the master key is on disk in the clear — either never encrypted, or
    /// wrapped by the macOS encryptor's documented fail-open shape, which is plaintext with a lid on.</summary>
    private static bool IsPlaintext(XDocument doc)
        => doc.Descendants().Any(e => e.Attribute(RequiresEncryption)?.Value == "true")
           || doc.Descendants(EncryptedSecret)
               .Any(e => e.Elements().FirstOrDefault()?.Name.LocalName == FailOpenWrapper);

    private static bool TryRewrap(
        XDocument source, IXmlEncryptor encryptor, out XDocument rewrapped, out string? failure)
    {
        rewrapped = new XDocument(source);
        failure = null;

        // A fail-open wrapper is lifted back to the plaintext element it hid, so the loop below can
        // encrypt it for real.
        foreach (var secret in rewrapped.Descendants(EncryptedSecret).ToList())
        {
            var wrapper = secret.Elements().FirstOrDefault();
            if (wrapper?.Name.LocalName != FailOpenWrapper)
            {
                continue;
            }

            var plain = wrapper.Elements().FirstOrDefault();
            if (plain is null)
            {
                failure = "a fail-open key wrapper carries no key element";
                return false;
            }

            secret.ReplaceWith(new XElement(plain));
        }

        while (true)
        {
            var target = rewrapped.Descendants().FirstOrDefault(e => e.Attribute(RequiresEncryption)?.Value == "true");
            if (target is null)
            {
                return true;
            }

            EncryptedXmlInfo info;
            try
            {
                info = encryptor.Encrypt(target);
            }
            catch (Exception ex)
            {
                failure = $"re-wrap failed: {ex.Message}";
                return false;
            }

            if (info.EncryptedElement.Name.LocalName == FailOpenWrapper)
            {
                // The macOS encryptor could not reach the Keychain; it handed back the plaintext with a
                // lid on. Writing that would claim a protection that is not there.
                failure = "re-wrap skipped: the protector is unavailable, so it would store plaintext";
                return false;
            }

            if (!RoundTrips(info, target, out var roundTripFailure))
            {
                failure = roundTripFailure;
                return false;
            }

            target.ReplaceWith(new XElement(
                EncryptedSecret,
                new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!),
                info.EncryptedElement));
        }
    }

    /// <summary>Decrypts what we are about to write and checks it is byte-for-byte the key we started
    /// from. A re-wrap that cannot be undone is a lost master key, so it never reaches the disk.</summary>
    private static bool RoundTrips(EncryptedXmlInfo info, XElement original, out string? failure)
    {
        failure = null;
        try
        {
            var decryptor = (IXmlDecryptor)Activator.CreateInstance(info.DecryptorType)!;
            var restored = decryptor.Decrypt(info.EncryptedElement);
            if (restored.ToString(SaveOptions.DisableFormatting)
                == original.ToString(SaveOptions.DisableFormatting))
            {
                return true;
            }

            failure = "re-wrap abandoned: the re-encrypted key did not decrypt back to the original";
            return false;
        }
        catch (Exception ex)
        {
            failure = $"re-wrap abandoned: the re-encrypted key would not decrypt back ({ex.Message})";
            return false;
        }
    }

    /// <summary>Writes the re-wrapped key owner-only via a temp file + replace, so a crash mid-write
    /// cannot leave a truncated key file where the master key used to be.</summary>
    private static bool TryWriteAtomically(string path, XDocument doc, out string? failure)
    {
        failure = null;
        var temp = path + ".rewrap";
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            doc.Save(temp);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = $"re-wrap could not be written: {ex.Message}";
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original key file is untouched either way.
            }

            return false;
        }
    }
}
