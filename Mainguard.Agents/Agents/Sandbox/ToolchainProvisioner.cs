using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// What a built per-repo toolchain layer is: the image ref to spawn from, and the provenance that
/// makes it checkable.
/// </summary>
/// <param name="ImageRef">The ref the jail is created from — a <c>sha256:</c> digest when the builder
/// can resolve one, exactly like the base image's pin.</param>
/// <param name="BaseDigest">The base-image digest this layer was built ON. The whole point of MG-27 is
/// that the bytes that were checked are the bytes that run; a derived layer keeps that only if it
/// records, and re-checks, which base it derives from.</param>
/// <param name="Ids">The toolchains in the layer.</param>
public sealed record ProvisionedToolchain(string ImageRef, string BaseDigest, ImmutableArray<string> Ids);

/// <summary>
/// The image-store operations <see cref="ToolchainProvisioner"/> needs, kept behind a seam so the
/// provisioner's decision logic (cache hit / cache miss / poisoned tag / failed build) is testable
/// without a Docker daemon — and so a future non-Docker engine can supply its own.
/// </summary>
public interface IToolchainImageBuilder
{
    /// <summary>The labels on <paramref name="imageRef"/>, or null when the image is absent.</summary>
    Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default);

    /// <summary>The immutable content digest <paramref name="imageRef"/> resolves to, or null.</summary>
    Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default);

    /// <summary>
    /// The ordered <c>RootFS.Layers</c> (diffIDs) of <paramref name="imageRef"/>, or null when absent.
    ///
    /// <para>This is the evidence behind <see cref="ToolchainProvisioner"/>'s base-inheritance check. A
    /// derived image's layer chain is its parent's chain plus whatever the new steps added, so "was this
    /// layer really built on that base?" is a prefix comparison over content-addressed diffIDs — a
    /// structural fact of the image format rather than a claim the builder makes about itself. Labels
    /// cannot do this job: a label is an arbitrary string whoever built the image chose.</para>
    /// </summary>
    Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default);

    /// <summary>
    /// Builds <paramref name="dockerfile"/> and tags it <paramref name="imageRef"/>, stamping
    /// <paramref name="labels"/>. Throws on a build failure — the caller turns that into the typed
    /// <see cref="ToolchainProvisioningException"/>.
    /// </summary>
    Task BuildAsync(
        string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default);

    /// <summary>
    /// The same build, with a sink for the engine's own output as it arrives.
    ///
    /// <para>The output is not a log here — it is <b>evidence of life</b>. This build runs for minutes
    /// inside a spawn RPC, and every watchdog upstream decides "alive or wedged?" from what it has heard
    /// recently; <see cref="ToolchainBuildHeartbeat"/> turns this stream into the lines that answer it.
    /// A default implementation forwards to the sink-less overload, so an implementation that has no
    /// output to offer (and every existing test double) is unaffected and simply reports nothing.</para>
    /// </summary>
    Task BuildAsync(
        string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels,
        IProgress<string>? engineOutput, CancellationToken ct = default)
        => BuildAsync(imageRef, dockerfile, labels, ct);

    /// <summary>
    /// Engine and image facts for a FAILURE message only — the daemon version, the storage driver, and
    /// what the engine actually holds for the base image. Never throws; returns empty when unavailable.
    ///
    /// <para>This exists because of a specific, expensive failure: CI produced
    /// <c>pull access denied for mainguard-agent-base</c> with a one-line build log, and nothing in the
    /// error said whether the base image was present, what identity the engine had for it, or which
    /// engine was speaking — so the failure could not be diagnosed from CI output at all. An error that
    /// says something failed without saying why sends the next investigation guessing.</para>
    /// </summary>
    Task<string> DescribeBuildContextAsync(
        string baseDigest, string baseImageName, CancellationToken ct = default) => Task.FromResult(string.Empty);
}

/// <summary>
/// Builds — once — the per-repo toolchain layer a repository's <c>.mainguard/toolchain</c> asks for,
/// and hands the spawn path an image ref to jail from.
///
/// <para><b>Where this runs, and why that is the whole egress answer.</b> The build is a
/// <c>docker build</c> issued by the daemon, on the VM's own network. It is NOT the jail's network:
/// the jail sits on a per-agent <c>internal</c> segment whose only other member is the egress proxy,
/// and it reaches the world solely through that proxy's default-deny allowlist with a pinned
/// NXDOMAIN-by-default resolver and an iptables backstop. Nothing in this file touches
/// <see cref="EgressAllowlist"/>. The recipes reach <c>github.com</c> and <c>cache.nixos.org</c> (nix)
/// and <c>builds.dotnet.microsoft.com</c> (.NET) — <b>at build time, from the VM</b>, which is exactly
/// where <c>images/mainguard-agent-base/Dockerfile</c> already reaches them to bake its own curated
/// toolchain. A jail can reach none of those, before or after this change, and in particular the git
/// host stays off the agent allowlist (A6) — which is precisely why the toolchain cannot be installed
/// from inside the jail and has to be a layer.</para>
///
/// <para><b>Timing (G-16).</b> This is a PROVISIONING-time build, on the spawn path before any
/// container exists. G-16 forbids a <c>docker build</c> during a live agent session because it severs
/// the PTY; there is no session yet when this runs. It is also why the layer is chosen at SPAWN and not
/// at verification: a live jail's image cannot be changed underneath it.</para>
///
/// <para><b>Cost.</b> First build for a given (base digest × declaration) pair is a real build — a
/// couple of minutes and about a gigabyte for the .NET SDK. Every spawn after that, for every agent on
/// every repo with the same declaration, is a label inspect and a digest resolve: two Docker API calls,
/// tens of milliseconds, no network. The cache key is content-addressed rather than repo-scoped
/// precisely so that N repos declaring <c>dotnet-10</c> share one layer instead of building N.</para>
/// </summary>
public sealed class ToolchainProvisioner
{
    /// <summary>The image repository per-repo toolchain layers are tagged under.</summary>
    public const string ImageName = "mainguard-agent-toolchain";

    /// <summary>Label: the base-image digest the layer was built on.</summary>
    public const string BaseDigestLabel = "mainguard.toolchain.base-digest";

    /// <summary>Label: the newline-joined toolchain ids in the layer.</summary>
    public const string SpecLabel = "mainguard.toolchain.spec";

    /// <summary>
    /// The human-readable line reported through <see cref="ToolchainProvisioner"/>'s progress sink when
    /// a layer actually has to be BUILT (never on a cache hit — a cache hit is not slow and saying
    /// "building" for it would train people to ignore the message).
    ///
    /// <para>It exists because this build is the one part of a coordinator start that takes minutes: a
    /// <c>dotnet-10</c> layer pulls ~2.9 GB, and it runs INSIDE the spawn call with nothing on the wire
    /// to say so. The UI showed a blank pane and then, after 45 s, "the coordinator isn't responding —
    /// use Stop to cancel and try again" — advice that killed the very build about to unblock it. This
    /// string is what turns that wait into progress, so it is written for the person waiting, not for a
    /// log grep (the log line next to it keeps the machine-readable ids/tag).</para>
    /// </summary>
    public static string BuildingMessage(ToolchainDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var ids = declaration.Normalized.Replace('\n', ',');
        return $"Building this repository's toolchain image ({ids}). The first start downloads a few GB "
             + "and takes several minutes — leave Mainguard running.";
    }

    /// <summary>
    /// The line a build reports every <see cref="ToolchainBuildHeartbeat.DefaultInterval"/> while it
    /// runs. <see cref="BuildingMessage"/> says a build started; this says it is STILL going, which is
    /// the only thing that distinguishes a healthy multi-gigabyte download from a daemon that died
    /// mid-build. Everything upstream re-arms its stall watchdog on a new line, so a build that keeps
    /// speaking cannot be declared unresponsive.
    /// </summary>
    /// <param name="elapsed">How long the build has been running.</param>
    /// <param name="engineLine">The engine's last output line, when it has produced any.</param>
    /// <param name="engineIdle">How long ago that line arrived.</param>
    public static string StillBuildingMessage(
        ToolchainDeclaration declaration, TimeSpan elapsed, string? engineLine, TimeSpan? engineIdle)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var ids = declaration.Normalized.Replace('\n', ',');
        var text = $"Still building this repository's toolchain image ({ids}) — "
                 + $"{ToolchainBuildHeartbeat.Describe(elapsed)} so far. Leave Mainguard running.";

        // The engine's own words when it has said anything. A silent stretch is NOT reported as a
        // problem: the .NET recipe's big step is a `curl -fsSL … | tar` that prints nothing for minutes,
        // so "quiet" is the normal shape of the slowest, healthiest part of the build.
        if (!string.IsNullOrWhiteSpace(engineLine))
        {
            text += engineIdle is { } idle && idle > TimeSpan.FromSeconds(30)
                ? $" Last from the engine ({ToolchainBuildHeartbeat.Describe(idle)} ago): {engineLine}"
                : $" Engine: {engineLine}";
        }

        return text;
    }

    /// <summary>
    /// The line reported while this spawn is WAITING for another spawn's identical build to finish.
    ///
    /// <para>It exists because the wait is otherwise indistinguishable from a hang: the second spawn does
    /// nothing at all for as long as the first one builds, and everything upstream reads "nothing for
    /// minutes" as a dead daemon. It also tells the truth about the situation — nothing is wrong, and
    /// there is exactly one build running rather than two.</para>
    /// </summary>
    public static string WaitingForBuildMessage(ToolchainDeclaration declaration, TimeSpan waited)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var ids = declaration.Normalized.Replace('\n', ',');
        return $"Waiting for this repository's toolchain image ({ids}) — another agent is already "
             + $"building it ({ToolchainBuildHeartbeat.Describe(waited)} so far). "
             + "Leave Mainguard running.";
    }

    /// <summary>The progress line reported once the layer is in hand and the jail can be created.</summary>
    public const string BuiltMessage = "Toolchain image ready — starting the sandbox.";

    private readonly IToolchainImageBuilder _builder;
    private readonly Action<string>? _log;
    private readonly IProgress<string>? _progress;
    private readonly ToolchainBuildGate _buildGate;
    private readonly TimeSpan? _heartbeatInterval;

    /// <param name="log">The daemon log sink (machine-readable lines: repo, tag, ids).</param>
    /// <param name="progress">The USER-FACING sink. Separate from <paramref name="log"/> on purpose:
    /// the daemon log wants the tag and the base digest, and the person staring at a blank pane wants
    /// to know that a multi-minute download is running and must not be interrupted. Optional, so every
    /// existing caller and every test double is unaffected.</param>
    /// <param name="buildGate">The gate that makes "one build per image, across every spawn" true.
    /// Defaults to <see cref="ToolchainBuildGate.Shared"/>, which is what the shipped spawn path uses —
    /// a per-provisioner gate could not serialise anything, because the launcher builds a new provisioner
    /// for every spawn. Injectable so a test can contend on an isolated gate.</param>
    /// <param name="heartbeatInterval">How often a running build reports in (tests shorten it).</param>
    public ToolchainProvisioner(
        IToolchainImageBuilder builder, Action<string>? log = null, IProgress<string>? progress = null,
        ToolchainBuildGate? buildGate = null, TimeSpan? heartbeatInterval = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _log = log;
        _progress = progress;
        _buildGate = buildGate ?? ToolchainBuildGate.Shared;
        _heartbeatInterval = heartbeatInterval;
    }

    /// <summary>
    /// Ensures the layer for <paramref name="declaration"/> over <paramref name="baseDigest"/> exists,
    /// and returns what to spawn from. Returns <c>null</c> for an empty declaration — the repo needs
    /// nothing beyond the base image and the caller stays on the base digest, unchanged.
    /// </summary>
    /// <exception cref="ToolchainProvisioningException">The layer could not be produced. Never
    /// swallowed, never degraded: see the exception's own summary.</exception>
    public async Task<ProvisionedToolchain?> EnsureAsync(
        string repoHandle, ToolchainDeclaration declaration, string? baseDigest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (declaration.IsEmpty)
        {
            return null;
        }

        if (!SandboxImageDigest.IsDigest(baseDigest))
        {
            // A layer built on an unpinned base would be a layer whose provenance stops at a mutable
            // tag — the exact gap MG-27 closed for the base image. Refuse rather than quietly widen it.
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"the agent base image did not resolve to a content digest (got '{baseDigest ?? "<null>"}'); "
                + "a per-repo toolchain layer is only built on a digest-pinned base.");
        }

        // Every declared id is resolved — an uncatalogued one is a typed refusal here, before anything
        // is built — but only the IMAGE-LAYER ones produce a layer. A runtime-mount toolchain
        // (ToolchainDelivery.RuntimeMount) is already installed in the VM because a human installed it,
        // and reaches the jail as a read-only bind mount; there is nothing to build for it, and building
        // an empty layer would be a gigabyte of cache and a multi-minute wait for no filesystem change.
        var recipes = declaration.Ids
            .Select(id => ToolchainCatalog.TryGet(id) ?? throw new UnknownToolchainException(repoHandle, id, ToolchainCatalog.KnownIds))
            .Where(r => r.Delivery == ToolchainDelivery.ImageLayer)
            .ToImmutableArray();

        if (recipes.IsEmpty)
        {
            // Declared, resolved, and nothing needs a layer. The caller stays on the base digest, and
            // the toolchains still reach the jail — through the mount, not the image.
            return null;
        }

        var tag = ImageTagFor(baseDigest!, declaration, recipes);
        var expectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BaseDigestLabel] = baseDigest!,
            [SpecLabel] = declaration.Normalized,
        };

        // One build per image, PROCESS-WIDE — see ToolchainBuildGate for why this cannot be a field on
        // this object. The key is the content-addressed tag, so two spawns that would produce the same
        // layer serialise and two that would not never block each other. The waiting caller keeps
        // reporting, because a silent wait is what "the coordinator is hung" looks like from outside.
        return await _buildGate.RunExclusiveAsync(
            tag,
            token => BuildOrReuseAsync(repoHandle, declaration, recipes, baseDigest!, tag, expectedLabels, token),
            onWaiting: waited =>
            {
                _log?.Invoke($"toolchain build wait: repo={repoHandle} image={tag} waited={waited}");
                _progress?.Report(WaitingForBuildMessage(declaration, waited));
            },
            waitHeartbeat: _heartbeatInterval,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>The body that runs under the gate: cache hit, or build then re-check. Separated only so
    /// the gate can own the waiting; every decision it makes is the one it made inline before.</summary>
    private async Task<ProvisionedToolchain?> BuildOrReuseAsync(
        string repoHandle, ToolchainDeclaration declaration, IReadOnlyList<ToolchainRecipe> recipes,
        string baseDigest, string tag, IReadOnlyDictionary<string, string> expectedLabels, CancellationToken ct)
    {
        var hit = await TryUseCachedAsync(tag, expectedLabels, ct).ConfigureAwait(false);
        if (hit is null)
        {
            var dockerfile = RenderDockerfile(baseDigest, recipes);
            _log?.Invoke($"toolchain build begin: repo={repoHandle} image={tag} ids={declaration.Normalized.Replace('\n', ',')}");
            // Reported BEFORE the build, not after: the whole point is to be visible during it.
            _progress?.Report(BuildingMessage(declaration));

            // …and kept audible FOR THE WHOLE BUILD. One announcement dates instantly: five minutes into
            // a 2.9 GB layer the surface has heard nothing recent and cannot tell healthy from wedged.
            // The engine's own output feeds this so the line can quote it, but the beat does not depend
            // on it — the slowest step is silent by construction.
            var heartbeat = new ToolchainBuildHeartbeat(
                _progress,
                (elapsed, line, idle) => StillBuildingMessage(declaration, elapsed, line, idle),
                _heartbeatInterval);
            try
            {
                await _builder.BuildAsync(tag, dockerfile, expectedLabels, heartbeat, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The engine context is gathered ONLY here, on the failure path, and appended to the
                // detail — the CI failure this guards against ("pull access denied", one line of
                // build log) was undiagnosable precisely because the message said what happened and
                // nothing about the engine that did it.
                var context = await SafeDescribeAsync(baseDigest, ct).ConfigureAwait(false);
                throw new ToolchainProvisioningException(
                    repoHandle, declaration.Ids, Describe(ex) + context);
            }
            finally
            {
                // Awaited, so no heartbeat can be reported after the build it describes has ended.
                await heartbeat.DisposeAsync().ConfigureAwait(false);
            }

            hit = await TryUseCachedAsync(tag, expectedLabels, ct).ConfigureAwait(false)
                  ?? throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                      $"the build of '{tag}' reported success but the image is absent or carries the wrong provenance labels."
                      + await SafeDescribeAsync(baseDigest, ct).ConfigureAwait(false));

            _log?.Invoke($"toolchain build ok: repo={repoHandle} image={hit}");
            _progress?.Report(BuiltMessage);
        }

        // The pin, PROVEN rather than requested. Everything above only decides which base we ASKED
        // the engine for; this checks what it actually produced, from the image format itself. It
        // runs on the cache-hit path too, so every spawn re-confirms the layer sits on the base the
        // preflight just verified — which also closes the window where the base moved between the
        // build and now.
        await VerifyBuiltOnBaseAsync(repoHandle, declaration, hit, baseDigest, ct).ConfigureAwait(false);

        return new ProvisionedToolchain(hit, baseDigest, declaration.Ids);
    }

    /// <summary>
    /// The content-addressed tag for a layer: <c>mainguard-agent-toolchain:&lt;16 hex&gt;</c> over the
    /// base digest AND the complete generated build spec.
    ///
    /// <para>All three inputs matter and each was learned the hard way. The <b>base digest</b>: a layer
    /// built on last week's base is a different artefact even when the id list is identical, and reusing
    /// it would silently unpin the base. The <b>declaration</b>: obviously. The <b>recipes</b>: hashing
    /// only the ids meant a fix to a recipe produced the same tag as the broken version, so the cache
    /// served the broken layer forever and the fix could never ship. That is not hypothetical — the
    /// first real-jail run of this feature found the <c>dotnet-10</c> recipe missing libicu, and
    /// correcting it did not change the id list. Hashing the rendered Dockerfile covers install steps,
    /// PATH, environment and ordering in one stroke, because the Dockerfile IS the build spec.</para>
    /// </summary>
    public static string ImageTagFor(string baseDigest, ToolchainDeclaration declaration) =>
        ImageTagFor(baseDigest, declaration, ResolveRecipes(declaration));

    /// <summary>Testable core — takes the recipes rather than looking them up, so "a recipe change
    /// changes the tag" is assertable without mutating the catalog.</summary>
    internal static string ImageTagFor(
        string baseDigest, ToolchainDeclaration declaration, IReadOnlyList<ToolchainRecipe> recipes)
    {
        var material = baseDigest + "\n" + declaration.Normalized + "\n" + RenderDockerfile(baseDigest, recipes);
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return ImageName + ":" + hex[..16];
    }

    private static IReadOnlyList<ToolchainRecipe> ResolveRecipes(ToolchainDeclaration declaration) =>
        declaration.Ids
            .Select(id => ToolchainCatalog.TryGet(id)
                ?? throw new UnknownToolchainException(string.Empty, id, ToolchainCatalog.KnownIds))
            // Same filter as EnsureAsync: the tag names what the LAYER contains, and a runtime-mount
            // toolchain contributes no layer content. Filtering in both places keeps the tag a faithful
            // content address rather than a hash over things the image does not hold.
            .Where(r => r.Delivery == ToolchainDelivery.ImageLayer)
            .ToImmutableArray();

    /// <summary>
    /// The generated Dockerfile. Deliberately boring and fully derived: the only inputs a repository
    /// contributes are the catalogued <i>ids</i>, so every line below comes from product source.
    /// </summary>
    internal static string RenderDockerfile(string baseDigest, IReadOnlyList<ToolchainRecipe> recipes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Mainguard ToolchainProvisioner — do not edit, do not commit.");

        // The FROM is the BARE digest, with no `<name>@` in front of it, and the difference is not
        // cosmetic. `name@sha256:…` is a CANONICAL REGISTRY REFERENCE: the engine looks for a local
        // image whose RepoDigests contains that exact pair and, finding none, tries to PULL it. A
        // locally built (or `docker load`ed) image has no registry manifest digest, so on a classic
        // overlay2 image store RepoDigests is empty and the build dies at step 1 with
        // `pull access denied for mainguard-agent-base`. Measured: that is exactly what CI did.
        //
        // It passed locally for a reason that does not exist on CI — this box runs the containerd image
        // store, which synthesises `RepoDigests=[mainguard-agent-base@sha256:<its own Id>]` for a
        // locally built image, so the registry reference happened to resolve. Two engines, opposite
        // behaviour, and the green one was green by accident.
        //
        // A bare digest is an IMAGE ID reference instead: resolved purely against the local image
        // store, on both stores, never a registry lookup and so never a pull. Verified locally that all
        // three forms resolve here and only this one can resolve on a store with empty RepoDigests.
        // The pin does not rest on this alone — VerifyBuiltOnBaseAsync proves inheritance after the
        // build from the layer chain itself.
        sb.AppendLine(CultureInfo.InvariantCulture, $"FROM {baseDigest}");
        sb.AppendLine();
        // The base image ends on USER agent; installs need root, and the layer must end back on the
        // non-root agent uid or the jail's whole hardening posture changes.
        sb.AppendLine("USER root");
        foreach (var recipe in recipes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"# --- {recipe.Id}: {recipe.Summary}");
            foreach (var step in recipe.InstallSteps)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"RUN {step}");
            }
        }

        // MUTATION: sb.AppendLine("USER agent");

        var pathEntries = recipes.SelectMany(r => r.PathEntries).ToArray();
        if (pathEntries.Length > 0)
        {
            // Prepended, but AFTER the adapters mount stays first is not a concern here: this simply
            // puts the per-repo toolchain ahead of the base image's curated one so a repo that declares
            // a language gets ITS version, not an incidental base-image collision.
            sb.AppendLine(CultureInfo.InvariantCulture, $"ENV PATH={string.Join(':', pathEntries)}:$PATH");
        }

        foreach (var (name, value) in recipes.SelectMany(r => r.Environment))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"ENV {name}={value}");
        }

        sb.AppendLine("WORKDIR /workspace");
        return sb.ToString();
    }

    /// <summary>
    /// A cache hit is an image that exists AND whose provenance labels match what we would have built.
    /// The label check is not ceremony: the tag is a local name, so "an image already holds this tag"
    /// is not evidence about its contents. A mismatch is treated as a miss and rebuilt over.
    /// </summary>
    private async Task<string?> TryUseCachedAsync(
        string tag, IReadOnlyDictionary<string, string> expected, CancellationToken ct)
    {
        var labels = await _builder.InspectLabelsAsync(tag, ct).ConfigureAwait(false);
        if (labels is null)
        {
            return null;
        }

        foreach (var (key, value) in expected)
        {
            if (!labels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                _log?.Invoke($"toolchain cache miss: image={tag} label '{key}' is '{actual ?? "<absent>"}', expected '{value}'");
                return null;
            }
        }

        // Same pin as the base image: resolve the ref to its digest and hand THAT back, so the layer we
        // just checked is the layer the container is created from.
        return await _builder.ResolveDigestAsync(tag, ct).ConfigureAwait(false) ?? tag;
    }

    /// <summary>
    /// Proves the layer was built on the verified base, from the layer chain rather than from anything
    /// the builder said about itself: a derived image's <c>RootFS.Layers</c> begins with its parent's,
    /// so the base's diffIDs must be an exact prefix of the layer's. Verified against real images
    /// (base 7 diffIDs, layer 9, first 7 identical).
    ///
    /// <para>Why this and not the label: <see cref="BaseDigestLabel"/> is a string the builder chose, so
    /// it answers "what did this image claim?" A diffID is content-addressed, so the prefix answers
    /// "what is this image made of?" Only the second one is a pin.</para>
    ///
    /// <para>An indeterminate answer is a FAILURE, never a skip. "We could not check" and "we checked
    /// and it was fine" must not produce the same outcome, or the check evaporates on exactly the
    /// engine where it was needed.</para>
    /// </summary>
    private async Task VerifyBuiltOnBaseAsync(
        string repoHandle, ToolchainDeclaration declaration, string layerRef, string baseDigest, CancellationToken ct)
    {
        var baseLayers = await _builder.RootFsLayersAsync(baseDigest, ct).ConfigureAwait(false);
        if (baseLayers is null || baseLayers.Count == 0)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"could not read the base image's layer chain for '{baseDigest}', so the layer's parentage "
                + "cannot be proven; refusing to jail from an unverified toolchain image."
                + await SafeDescribeAsync(baseDigest, ct).ConfigureAwait(false));
        }

        var layerLayers = await _builder.RootFsLayersAsync(layerRef, ct).ConfigureAwait(false);
        if (layerLayers is null || layerLayers.Count < baseLayers.Count)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"the toolchain layer '{layerRef}' reports {layerLayers?.Count.ToString(CultureInfo.InvariantCulture) ?? "no"} "
                + $"filesystem layers, fewer than the {baseLayers.Count} of the base it must be built on."
                + await SafeDescribeAsync(baseDigest, ct).ConfigureAwait(false));
        }

        for (var i = 0; i < baseLayers.Count; i++)
        {
            if (!string.Equals(baseLayers[i], layerLayers[i], StringComparison.Ordinal))
            {
                throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                    $"the toolchain layer '{layerRef}' was NOT built on the verified base '{baseDigest}' — "
                    + $"its filesystem layer {i} is '{layerLayers[i]}' where the base has '{baseLayers[i]}'."
                    + await SafeDescribeAsync(baseDigest, ct).ConfigureAwait(false));
            }
        }
    }

    private async Task<string> SafeDescribeAsync(string baseDigest, CancellationToken ct)
    {
        try
        {
            var text = await _builder
                .DescribeBuildContextAsync(baseDigest, SandboxImageVersions.AgentBaseName, ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? string.Empty : "\n" + text;
        }
        catch (Exception ex)
        {
            // Diagnostics must never replace the failure they describe.
            return $"\n---- engine ----\n(could not be gathered: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string Describe(Exception ex)
    {
        var text = ex.Message?.Trim() ?? ex.GetType().Name;
        const int max = 2000;
        return text.Length <= max ? text : string.Concat(text.AsSpan(text.Length - max), "");
    }
}
