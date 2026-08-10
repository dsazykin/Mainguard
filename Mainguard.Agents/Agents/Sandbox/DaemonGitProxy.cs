using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>An allowlisted <c>host + org</c> prefix the daemon git proxy may fetch from (A6).</summary>
public sealed record GitProxyPrefix(string Host, string Org)
{
    public bool Matches(string host, string org) =>
        string.Equals(Host, host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Org, org, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A fetch request arriving at the daemon git proxy from a sandbox.</summary>
public sealed record GitProxyRequest(string Service, string Host, string Org, string Repo, string AgentId);

/// <summary>The result of a successful daemon-side fetch (A6).</summary>
public sealed record GitFetchResult(long Bytes);

/// <summary>
/// A6 — the daemon-mediated, <b>read-only</b> git endpoint: the only path from a sandbox to a git
/// host. It performs <c>upload-pack</c> (fetch/clone) daemon-side, with the daemon's credentials, and
/// <b>only</b> for allowlisted <c>host + org</c> prefixes (so the sandbox never holds git-host
/// credentials — the P2-06 quarantine holds).
///
/// <para><b>Push is structurally impossible.</b> There is no <c>receive-pack</c> handler and no push
/// method on this type — the only git service the proxy speaks is <c>git-upload-pack</c>. Any other
/// service (a <c>git-receive-pack</c> push attempt) hits the single refusal branch below: it is
/// audited (<c>egress_denied</c>) and transparency-logged, then a typed
/// <see cref="GitProxyRefusedException"/> is thrown. No ref is ever moved.</para>
/// </summary>
public sealed class DaemonGitProxy
{
    /// <summary>The one git service the proxy speaks: fetch/clone only.</summary>
    public const string GitUploadPack = "git-upload-pack";

    /// <summary>The audit type for every denied egress attempt (feeds P2-44).</summary>
    public const string EgressDeniedEvent = "egress_denied";

    private readonly List<GitProxyPrefix> _allowedPrefixes;
    private readonly IAuditLog _audit;
    private readonly INetworkTransparencyLog _transparency;
    private readonly Func<GitProxyRequest, GitFetchResult> _fetchRunner;

    /// <param name="allowedPrefixes">The per-repo allowlisted host+org prefixes (the repo's own org is the default entry).</param>
    /// <param name="audit">G-17 audit sink; every refusal appends an <c>egress_denied</c> event.</param>
    /// <param name="transparency">P2-17 sink; every fetch and refusal emits a line.</param>
    /// <param name="fetchRunner">Performs the actual daemon-side fetch (injected so the pure tests do not spawn git).</param>
    public DaemonGitProxy(
        IEnumerable<GitProxyPrefix> allowedPrefixes,
        IAuditLog audit,
        INetworkTransparencyLog transparency,
        Func<GitProxyRequest, GitFetchResult> fetchRunner)
    {
        _allowedPrefixes = allowedPrefixes?.ToList() ?? throw new ArgumentNullException(nameof(allowedPrefixes));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _transparency = transparency ?? throw new ArgumentNullException(nameof(transparency));
        _fetchRunner = fetchRunner ?? throw new ArgumentNullException(nameof(fetchRunner));
    }

    /// <summary>The allowlisted prefixes (snapshot).</summary>
    public IReadOnlyList<GitProxyPrefix> AllowedPrefixes => _allowedPrefixes.ToArray();

    /// <summary>
    /// Handle a git smart-http service request. Fetch (upload-pack) for an allowlisted prefix
    /// succeeds; every other case is refused + audited + transparency-logged.
    /// </summary>
    public GitFetchResult ForwardService(GitProxyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // STRUCTURAL push refusal: the proxy speaks ONLY git-upload-pack. There is no receive-pack
        // code path — a push (git-receive-pack) or any other service falls straight through to the
        // refusal. This branch is the entire push story: deny, audit, transparency, throw.
        if (!string.Equals(request.Service, GitUploadPack, StringComparison.Ordinal))
        {
            Deny(request, verdict: EgressVerdict.Denied,
                reason: $"non-fetch git service '{request.Service}' is refused (A6: no receive-pack/push code path exists).");
            throw new GitProxyRefusedException(
                $"Git service '{request.Service}' is not permitted; the daemon git proxy is fetch-only (A6). No ref was moved.");
        }

        // Prefix allowlist: only host+org prefixes the repo declared may be fetched.
        if (!IsPrefixAllowed(request.Host, request.Org))
        {
            Deny(request, verdict: EgressVerdict.Denied,
                reason: $"host+org prefix '{request.Host}/{request.Org}' is not on the allowlist (A6).");
            throw new GitProxyRefusedException(
                $"Fetch of '{request.Host}/{request.Org}/{request.Repo}' is not allowlisted (A6).");
        }

        // Allowed fetch: perform it daemon-side (daemon creds, not the sandbox's) and log transparency.
        var result = _fetchRunner(request);
        _transparency.Record(TransparencyLine.Now(
            "git_fetch", request.Host, $"{request.Org}/{request.Repo}", request.AgentId, result.Bytes, EgressVerdict.Allowed));
        return result;
    }

    /// <summary>Adds an allowlisted host+org prefix (per-repo config; e.g. the repo's own org).</summary>
    public void AllowPrefix(GitProxyPrefix prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!_allowedPrefixes.Any(p => p.Matches(prefix.Host, prefix.Org)))
            _allowedPrefixes.Add(prefix);
    }

    private bool IsPrefixAllowed(string host, string org) =>
        _allowedPrefixes.Any(p => p.Matches(host, org));

    private void Deny(GitProxyRequest request, EgressVerdict verdict, string reason)
    {
        _audit.Append(new AuditEvent(EgressDeniedEvent, new Dictionary<string, string>
        {
            ["agent"] = request.AgentId,
            ["service"] = request.Service,
            ["host"] = request.Host,
            ["org"] = request.Org,
            ["repo"] = request.Repo,
            ["reason"] = reason,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        }));
        _transparency.Record(TransparencyLine.Now(
            "git_fetch", request.Host, $"{request.Org}/{request.Repo} ({request.Service})", request.AgentId, 0, verdict));
    }
}
