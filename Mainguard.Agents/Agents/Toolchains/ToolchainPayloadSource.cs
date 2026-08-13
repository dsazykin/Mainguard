using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;

namespace Mainguard.Agents.Agents.Toolchains;

/// <summary>
/// Where a toolchain's pinned payload bytes come from.
///
/// <para><b>Why this seam exists at all.</b> The install path used to fetch the payload by shelling
/// <c>curl</c> INTO the MainguardEnv VM. The VM has no <c>curl</c> — <c>build/mainguardos/packages.pinned.txt</c>
/// pins neither it nor <c>wget</c>, and a live VM confirms it — so every install failed with
/// <c>curl: command not found</c> (exit 127). It passed CI for the whole of its life because every test
/// substituted a scripted <see cref="IAdapterInstallHost"/> that answered <c>curl</c> with exit 0, so
/// "does this command exist in the VM" was never a fact under test. Fetching HERE, on the host, in .NET,
/// removes the question: there is no in-VM binary to depend on, and the one that IS depended on
/// (<c>base64</c>, via <see cref="IAdapterInstallHost.StagePayloadAsync"/>) is the one the adapter
/// install path has been exercising against the real VM all along.</para>
///
/// <para>It is an interface rather than a bare <see cref="HttpClient"/> call so a test can hand the
/// channel bytes without a network — which is what lets the install policy (verify, then stage, then
/// unpack, then prove it runs) be driven end to end over real tools.</para>
/// </summary>
public interface IToolchainPayloadSource
{
    /// <summary>Fetches the payload at <paramref name="url"/>. Implementations must refuse a non-HTTPS
    /// URL (see <see cref="PinnedPayloadTransport.RequireHttps"/>).</summary>
    Task<byte[]> FetchAsync(Uri url, CancellationToken ct);
}

/// <summary>
/// The production source: an ordinary HTTPS GET from the host, over the same transport rule the adapter
/// channel's payload fetches obey.
///
/// <para>The bytes land in memory and are hashed before anything is transferred into the VM, which is
/// strictly stronger than the in-VM <c>sha256sum</c> it replaces: a payload that fails the pin was never
/// written anywhere the VM can see it, so there is nothing to discard and nothing to have raced with.</para>
/// </summary>
public sealed class HttpsToolchainPayloadSource : IToolchainPayloadSource
{
    private readonly HttpClient _http;

    /// <param name="handler">A message handler, for tests that want to stub the transport rather than
    /// the source. Production passes nothing.</param>
    public HttpsToolchainPayloadSource(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);

        // A pinned toolchain is ~100 MB over a link Mainguard does not control; the 100-second default
        // is a timeout on a healthy download, not on a broken one.
        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public async Task<byte[]> FetchAsync(Uri url, CancellationToken ct) =>
        await _http.GetByteArrayAsync(PinnedPayloadTransport.RequireHttps(url), ct).ConfigureAwait(false);
}
