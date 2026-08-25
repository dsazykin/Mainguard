using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Mainguard.Git.Security;

/// <summary>
/// macOS at-rest protection for the DataProtection key ring (the DPAPI analogue Windows already
/// has): the key-ring XML is AES-256-GCM-encrypted with a master key that lives ONLY in the
/// user's login Keychain. Keychain access goes through <c>/usr/bin/security</c> deliberately —
/// the item's ACL then names the Apple-signed CLI itself, so ad-hoc-rebuilt dev binaries never
/// trigger per-build permission prompts, and no Security.framework interop joins the build.
///
/// <para>Fail-open by design, matching the pre-existing posture: when the Keychain is
/// unavailable (headless CI, locked keychain) encryption falls back to the plaintext element the
/// platform previously stored anyway — secrets must survive, hardening is best-effort. Keys
/// written before this change still load (DataProtection reads unencrypted key XML natively).</para>
/// </summary>
public sealed class MacKeychainXmlEncryptor : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var master = MacKeychainMasterKey.GetOrCreate();
        if (master is null)
        {
            // The documented fallback: plaintext under the data root's ACL — exactly what every
            // non-Windows platform stored before this encryptor existed.
            return new EncryptedXmlInfo(
                new XElement("unencryptedKey", plaintextElement),
                typeof(MacKeychainXmlDecryptor));
        }

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
            new XElement("macKeychainEncryptedKey",
                new XComment(" AES-256-GCM; master key in the login Keychain (com.mainguard.keyring) "),
                new XElement("value", Convert.ToBase64String(payload))),
            typeof(MacKeychainXmlDecryptor));
    }
}

/// <summary>The decryptor half — resolved by type name from the key XML.</summary>
public sealed class MacKeychainXmlDecryptor : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        // The fail-open shape wraps the original element unencrypted.
        if (encryptedElement.Name.LocalName == "unencryptedKey")
        {
            var inner = encryptedElement.Elements();
            foreach (var element in inner) return element;
            throw new CryptographicException("unencryptedKey element carries no key.");
        }

        var master = MacKeychainMasterKey.GetOrCreate()
            ?? throw new CryptographicException(
                "The Mainguard keyring master key is not available from the login Keychain, but the "
                + "key ring was encrypted with it. Unlock the login keychain and retry.");

        var payload = Convert.FromBase64String(
            encryptedElement.Element("value")?.Value
            ?? throw new CryptographicException("macKeychainEncryptedKey element carries no value."));

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

/// <summary>The Keychain-held master key, created on first use. Null when the Keychain cannot
/// serve it (non-macOS, headless, locked) — callers fail open per the type doc above.</summary>
internal static class MacKeychainMasterKey
{
    private const string Service = "com.mainguard.keyring";
    private const string Account = "keyring-master";

    private static byte[]? _cached;

    internal static byte[]? GetOrCreate()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        if (_cached is not null) return _cached;

        try
        {
            var existing = Security("find-generic-password", "-s", Service, "-a", Account, "-w");
            if (existing.ExitCode == 0 && existing.StdOut.Trim() is { Length: 64 } hex)
            {
                return _cached = Convert.FromHexString(hex);
            }

            var fresh = RandomNumberGenerator.GetBytes(32);
            var freshHex = Convert.ToHexString(fresh).ToLowerInvariant();
            var add = Security("add-generic-password", "-s", Service, "-a", Account, "-w", freshHex, "-U");
            if (add.ExitCode != 0) return null;

            return _cached = fresh;
        }
        catch
        {
            return null;
        }
    }

    private static (int ExitCode, string StdOut) Security(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var security = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /usr/bin/security.");
        var stdout = security.StandardOutput.ReadToEnd();
        security.WaitForExit(10_000);
        return (security.HasExited ? security.ExitCode : -1, stdout);
    }
}
