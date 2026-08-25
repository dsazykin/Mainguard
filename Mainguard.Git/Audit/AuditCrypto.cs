using System;
using System.Security.Cryptography;
using System.Text;
using Mainguard.Git.Security;

namespace Mainguard.Git.Audit;

/// <summary>
/// P2-15 at-rest payload encryption: AES-256-GCM over the canonical envelope, master key held in
/// the OS keyring via <see cref="ISecureKeyStore"/> (generated on first use). The hash chain is
/// computed over the PLAINTEXT canonical envelope — encryption protects confidentiality of the
/// prompt/output store, the chain protects integrity; the two are deliberately independent so
/// verify can fail loudly on tamper even when decryption succeeds, and vice versa.
/// </summary>
public sealed class AuditCrypto
{
    /// <summary>The keyring entry holding the base64 256-bit master key.</summary>
    public const string KeyName = "audit-payload-key";

    /// <summary>The key id stamped on rows encrypted by this version of the scheme.</summary>
    public const string CurrentKeyId = "v1";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AuditCrypto(ISecureKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);

        var stored = keyStore.Get(KeyName);
        if (stored is null)
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            keyStore.Set(KeyName, Convert.ToBase64String(fresh));
            _key = fresh;
        }
        else
        {
            _key = Convert.FromBase64String(stored);
        }
    }

    /// <summary>Encrypts <paramref name="plaintext"/> → nonce ‖ tag ‖ ciphertext.</summary>
    public byte[] Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plainBytes.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        return blob;
    }

    /// <summary>Decrypts a nonce ‖ tag ‖ ciphertext blob back to the canonical envelope text.</summary>
    public string Decrypt(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Audit payload blob is shorter than nonce + tag.");
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
