namespace Mainguard.Git.Exceptions;

/// <summary>
/// A remote operation over an <b>SSH-form</b> remote was refused by the key exchange — a locked
/// (passphrase-protected) key with nothing to unlock it, a key the host does not accept, or no usable
/// key at all.
///
/// <para><b>Why this is not <see cref="AuthenticationRequiredException"/>.</b> The two failures have
/// completely different remedies and this type exists to keep them apart. An HTTPS auth failure means
/// "store a token for this host", and the UI answers it by opening Accounts with the host pre-filled.
/// An SSH failure means "your key is locked or not trusted", and a personal access token fixes exactly
/// none of it. Every SSH failure used to be classified as the former — git's stderr says
/// <c>Permission denied (publickey)</c>, which the generic classifier matched on — so a passphrase-
/// protected key sent the user to a token dialog that could not help, over and over.</para>
///
/// <para>This type was previously declared and caught but <b>never thrown from anywhere</b>, which is
/// why that misrouting went unnoticed: the branch meant to handle it was dead code.</para>
/// </summary>
public class SshAuthenticationException : System.Exception
{
    public SshAuthenticationException(string message) : base(message) { }

    public SshAuthenticationException(string message, System.Exception inner) : base(message, inner) { }

    /// <summary>The private key Mainguard resolved for this remote, when it could resolve one. Null when
    /// no usable key was found in <c>~/.ssh</c> at all — a different remedy (generate/add a key) from a
    /// key that exists but would not open.</summary>
    public string? KeyPath { get; init; }

    /// <summary>True when a passphrase for <see cref="KeyPath"/> IS stored in the keyring, so the key is
    /// known to be encrypted and the stored secret did not get it open (or never reached ssh).</summary>
    public bool HasStoredPassphrase { get; init; }
}
