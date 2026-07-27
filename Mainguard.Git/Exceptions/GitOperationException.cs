namespace Mainguard.Git.Exceptions;

/// <summary>
/// General-purpose Git failure that does not map to a more specific type
/// (branch/commit not found, nothing to amend, a failed CLI fallback, etc.).
///
/// <para><b>Host REST failures carry their cause structurally.</b> When this comes from the shared host
/// transport it also carries the HTTP status (<see cref="HostStatusCode"/>), or the fact that the host was
/// never reached (<see cref="HostUnreachable"/>). Without them, a caller that needs to tell "you lack
/// permission" from "the pull request head moved under you" from "you are offline" has only the host's
/// English prose to go on — which changes, is localized on some hosts, and reads alike for causes that
/// demand opposite responses from the user. The P2-12 external merge branches on these; every other caller
/// ignores them and catches exactly the exception it always caught, with the same message.</para>
///
/// <para>They are properties rather than a derived exception type deliberately: a subclass would silently
/// break every <c>Assert.Throws&lt;GitOperationException&gt;</c> in the suite (xunit matches the type
/// exactly), which would have meant editing a dozen tests across unrelated host features to make one
/// feature compile.</para>
/// </summary>
public class GitOperationException : MainguardException
{
    public GitOperationException(string message) : base(message) { }
    public GitOperationException(string message, System.Exception inner) : base(message, inner) { }

    /// <summary>The HTTP status a host REST call returned (401/403/404/405/409/422/5xx…), or null when
    /// this failure did not come from a host response.</summary>
    public int? HostStatusCode { get; init; }

    /// <summary>True when the host could not be reached at all — DNS, TLS, connection reset, offline.
    /// Distinct from any <see cref="HostStatusCode"/>, because nothing was attempted on the host: the
    /// honest response is "try again", never "the request was refused".</summary>
    public bool HostUnreachable { get; init; }
}
