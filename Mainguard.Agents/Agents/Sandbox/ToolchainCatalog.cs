using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// How a toolchain reaches a jail. Both kinds are equally curated and equally closed to repository
/// input; they differ only in what the artefact needs in order to exist.
/// </summary>
public enum ToolchainDelivery
{
    /// <summary>
    /// Built into a per-repo image layer on the spawn path by <see cref="ToolchainProvisioner"/>.
    ///
    /// <para>Required whenever the toolchain needs something a bind mount cannot carry: system packages
    /// from apt (<c>dotnet-10</c> aborts at startup without <c>libicu72</c>, and every headless Avalonia
    /// test dies without <c>libfontconfig1</c>) or the image's baked nix store (the nix-sourced recipes).
    /// Nothing about that is a user choice — it is a property of the artefact — so these are built on
    /// demand when a repository declares them, exactly as before.</para>
    /// </summary>
    ImageLayer,

    /// <summary>
    /// Installed into the VM by <see cref="Toolchains.ToolchainChannel"/> when a HUMAN asks for it, and
    /// bind-mounted READ-ONLY into every jail thereafter.
    ///
    /// <para>Available to any toolchain that ships as a self-contained relocatable tarball — no root, no
    /// system packages, no build. That is a real and common shape (CPython via python-build-standalone,
    /// the official Node and Go tarballs), and it is the shape that lets the user manage the set: an
    /// install is a directory, a removal is a <c>rm -rf</c>, and neither needs an image rebuild (G-16).</para>
    /// </summary>
    RuntimeMount,
}

/// <summary>
/// One curated, product-owned toolchain recipe. A repository's <c>.mainguard/toolchain</c> can name
/// <see cref="Id"/>; it can never supply any other field. That asymmetry is the entire security design
/// of the feature — see <see cref="ToolchainCatalog"/>.
/// </summary>
/// <param name="Id">The stable id a repo declares (kebab-case; matches <see cref="ToolchainCatalog.IdPattern"/>).</param>
/// <param name="Summary">Human copy for the UI/logs — what this toolchain is.</param>
/// <param name="InstallSteps">The Dockerfile <c>RUN</c> bodies, executed as root in the per-repo layer.
/// Every artefact fetched from outside the pinned base image is version-addressed AND checksum-verified,
/// or resolved from the pinned nixpkgs revision — the same discipline
/// <c>images/mainguard-agent-base/Dockerfile</c> holds itself to.</param>
/// <param name="PathEntries">Directories prepended to the jail's <c>PATH</c>.</param>
/// <param name="Environment">Extra environment the toolchain needs (never secrets — this is baked into
/// an image layer and is world-readable inside the jail).</param>
/// <param name="Probe">The argv that proves the toolchain landed, run inside the built image.</param>
/// <param name="BuildEgressHosts">The hosts the <b>build</b> reaches, for the record. These are reached
/// by the daemon's <c>docker build</c> on the VM's own network — they are NOT added to any jail's
/// egress allowlist and no jail can reach them (see <see cref="ToolchainProvisioner"/>).</param>
public sealed record ToolchainRecipe(
    string Id,
    string Summary,
    ImmutableArray<string> InstallSteps,
    ImmutableArray<string> PathEntries,
    ImmutableArray<KeyValuePair<string, string>> Environment,
    ImmutableArray<string> Probe,
    ImmutableArray<string> BuildEgressHosts,
    ToolchainDelivery Delivery = ToolchainDelivery.ImageLayer);

/// <summary>
/// The <b>closed</b> set of toolchains a repository may ask for.
///
/// <para><b>Why a catalog and not a package list.</b> The declaration file lives in the repo, and an
/// agent can write to its own branch. If the file said "install these packages", then adding a line
/// would be a way to run code of the agent's choosing inside its jail at provision time — before any
/// verification and before any human looks at the diff. Every mitigation for that (a registry
/// allowlist, a name grammar, a checksum requirement) still leaves the file describing an
/// <i>installation</i>, and installations run install scripts.</para>
///
/// <para>So the file does not describe an installation. It <b>selects</b> one, from this table, by id.
/// The recipes — the URLs, the checksums, the nixpkgs revision, the commands — are product source,
/// reviewed and versioned here. The strongest thing a repository can express is "this project is a
/// .NET project"; it cannot express <i>how</i> .NET is obtained. An id that is not in this table is a
/// typed <c>UnknownToolchainException</c>, never a passthrough and never a silent skip.</para>
///
/// <para>Combined with the resolution rule in <see cref="ToolchainDeclarationResolver"/> — only the
/// <b>main-side baseline</b> is ever provisioned — an agent's branch edit cannot cause even a
/// <i>catalogued</i> toolchain to be installed. It can only be flagged, and the flag blocks the
/// merge.</para>
/// </summary>
public static class ToolchainCatalog
{
    /// <summary>
    /// The nixpkgs revision the nix-sourced recipes resolve against. It is deliberately the SAME
    /// revision <c>images/mainguard-agent-base/Dockerfile</c> bakes its curated toolchain from
    /// (<c>ARG NIXPKGS_REV</c>), so the per-repo layer and the base image draw from one pinned
    /// universe rather than two that can drift apart.
    /// <c>ToolchainDeclarationTests.CatalogNixpkgsRevision_MatchesTheAgentBaseImage</c> reads the
    /// Dockerfile and fails if these two ever disagree.
    /// </summary>
    public const string NixpkgsRev = "50ab793786d9de88ee30ec4e4c24fb4236fc2674";

    /// <summary>Where a per-repo toolchain layer installs, outside the base image's <c>/opt/toolchain</c>
    /// so a layer can never shadow or corrupt the curated base tools.</summary>
    public const string InstallRoot = "/opt/mainguard-toolchain";

    /// <summary>The id grammar. Lower-case, digits, single dashes — narrow on purpose: an id is a
    /// lookup key in this table, so anything that could be read as a path, a flag, a URL or a shell
    /// fragment is rejected at parse time rather than relied upon to miss the table.</summary>
    public const string IdPattern = "^[a-z][a-z0-9]*(-[a-z0-9]+)*$";

    /// <summary>An upper bound on how many toolchains one repo may declare. A verification jail needs a
    /// language, not a distribution; a file listing thirty entries is a mistake or an attack, and either
    /// way a bounded refusal beats an hour-long build.</summary>
    public const int MaxDeclaredToolchains = 4;

    private static ToolchainRecipe Nix(string id, string summary, string[] attrs, string[] probe) =>
        new(
            Id: id,
            Summary: summary,
            InstallSteps: ImmutableArray.Create(
                $"nix profile install --profile {InstallRoot}/{id} "
                + string.Join(" ", attrs.Select(a => $"\"github:NixOS/nixpkgs/{NixpkgsRev}#{a}\""))
                + $" && chown -R agent:agent /nix {InstallRoot}/{id}"),
            PathEntries: ImmutableArray.Create($"{InstallRoot}/{id}/bin"),
            Environment: ImmutableArray<KeyValuePair<string, string>>.Empty,
            Probe: ImmutableArray.Create(probe),
            // nix resolves the flake from the git host and substitutes from the binary cache. Both are
            // reached by the BUILD, on the VM's network, never by a jail.
            BuildEgressHosts: ImmutableArray.Create("github.com", "cache.nixos.org"));

    // --- .NET ------------------------------------------------------------------------------------
    // Not nix-sourced, and the reason is worth recording: nixpkgs at the pinned revision above carries
    // dotnet-sdk_10 = 10.0.100-preview.3, and Mainguard's own global.json pins 10.0.301 with
    // `rollForward: latestPatch` — a different feature band is refused, so the nixpkgs build would not
    // run this repo's tests at all. The official SDK tarball is version-addressed and Microsoft
    // publishes a sha512 next to it, so pinning it gives the exact band AND the same
    // fetch-then-verify-then-execute discipline the base image applies to the Nix and Devbox binaries.
    // Refresh: bump the version and paste the published `.sha512`.
    private const string DotnetSdkVersion = "10.0.301";

    private const string DotnetSdkSha512 =
        "cfbeec3a3a1d3ad3e168e37a77c4cc26c23125acd84a86d014047da3ecffce4c3"
        + "68a9acac4d7c950a047fa3d98989ce8aea69f8e5842cb6d330e8911e1c335a7";

    private static readonly ToolchainRecipe Dotnet10 = new(
        Id: "dotnet-10",
        Summary: $".NET SDK {DotnetSdkVersion} (linux-x64, official build, sha512-pinned)",
        InstallSteps: ImmutableArray.Create(
            // libicu is NOT optional and its absence is not a warning. The hardened base is
            // debian-bookworm-slim, which ships no ICU, and .NET's globalization initialiser calls
            // Environment.FailFast when it cannot find one — so `dotnet --version` dies with SIGABRT
            // (exit 134) before Main runs. Measured in a real jail: the layer built, the digest pinned,
            // and every dotnet invocation aborted. The alternative,
            // DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, "fixes" it by silently changing culture,
            // collation and time-zone behaviour underneath the very test suite this toolchain exists to
            // run — a verification jail must not quietly disagree with the developer's machine about
            // what string comparison means. The apt fetch is signed-and-checksummed by apt itself, the
            // same discipline (and the same archive) the base image's own apt line relies on.
            //
            // libfontconfig1 is the same class of finding, one layer further along, and it was found the
            // same way — by running a real repository's real verify command in a real jail. With the
            // cache in place `dotnet test Mainguard.slnx --configuration Release` finally RESTORED and
            // BUILT inside the jail, and then 169 of 2716 tests failed identically:
            // `libfontconfig.so.1: cannot open shared object file` → SkiaSharp's native load fails →
            // every Avalonia headless render test dies in a type initialiser. It is not a Mainguard
            // quirk: Skia is how Avalonia, MAUI and every headless-rendering .NET suite draws, and
            // bookworm-slim ships no fontconfig. A repository cannot fix this itself — the catalog is a
            // CLOSED set by design (a declaration that could describe an installation would be an
            // install-time arbitrary-code-execution surface in a file an agent can write) — so a
            // catalogued ".NET SDK" that cannot run a mainstream .NET test suite is the catalog's bug,
            // not the repository's. Two small packages, from the same signed archive as the line above.
            "apt-get update && apt-get install -y --no-install-recommends libicu72 libfontconfig1 "
            + "&& rm -rf /var/lib/apt/lists/*",
            "curl --proto '=https' --tlsv1.2 -fsSL -o /tmp/dotnet-sdk.tar.gz "
            + $"\"https://builds.dotnet.microsoft.com/dotnet/Sdk/{DotnetSdkVersion}/dotnet-sdk-{DotnetSdkVersion}-linux-x64.tar.gz\""
            + $" && echo \"{DotnetSdkSha512}  /tmp/dotnet-sdk.tar.gz\" | sha512sum -c -"
            + $" && mkdir -p {InstallRoot}/dotnet-10"
            + $" && tar -xzf /tmp/dotnet-sdk.tar.gz -C {InstallRoot}/dotnet-10"
            + " && rm -f /tmp/dotnet-sdk.tar.gz"
            + $" && chown -R agent:agent {InstallRoot}/dotnet-10"),
        PathEntries: ImmutableArray.Create($"{InstallRoot}/dotnet-10"),
        Environment: ImmutableArray.Create(
            new KeyValuePair<string, string>("DOTNET_ROOT", $"{InstallRoot}/dotnet-10"),
            // A verification jail is not a place for first-run banners or usage telemetry: both write to
            // $HOME (a 256 MiB tmpfs) and both add latency to every single run.
            new KeyValuePair<string, string>("DOTNET_NOLOGO", "1"),
            new KeyValuePair<string, string>("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
            new KeyValuePair<string, string>("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")),
        // PROBE M21 (DO NOT MERGE): the toolchain-presence probe is weakened to something that always
        // succeeds, so a jail WITHOUT the declared toolchain reports as provisioned.
        Probe: ImmutableArray.Create("true"),
        BuildEgressHosts: ImmutableArray.Create("builds.dotnet.microsoft.com", "deb.debian.org"));

    /// <summary>The recipes that are built into a per-repo image layer on the spawn path.</summary>
    private static readonly ToolchainRecipe[] ImageLayerRecipes =
    {
        Dotnet10,
        Nix("rust-stable", "Rust (rustc + cargo) from the pinned nixpkgs",
            new[] { "rustc", "cargo" }, new[] { "cargo", "--version" }),
        Nix("jdk-21", "Temurin-class JDK 21 + Maven from the pinned nixpkgs",
            new[] { "jdk21", "maven" }, new[] { "java", "-version" }),
        Nix("ruby-3", "Ruby 3 + bundler from the pinned nixpkgs",
            new[] { "ruby_3_3", "bundler" }, new[] { "ruby", "--version" }),
        Nix("php-8", "PHP 8 + composer from the pinned nixpkgs",
            new[] { "php", "phpPackages.composer" }, new[] { "php", "--version" }),
    };

    /// <summary>
    /// A curated manifest entry, viewed as a catalog recipe so that ONE table answers "may a repository
    /// declare this id?" regardless of how the toolchain is delivered.
    ///
    /// <para>It carries no <see cref="ToolchainRecipe.InstallSteps"/> and no
    /// <see cref="ToolchainRecipe.BuildEgressHosts"/>, and that is the point rather than an omission:
    /// nothing is built, so there is no build to reach the network. The install happened earlier, in the
    /// VM, because a human asked for it.</para>
    ///
    /// <para>The paths and environment are resolved against the jail's read-only MOUNT
    /// (<see cref="Toolchains.ToolchainPaths.SandboxMount"/>), which is where a container will find it.</para>
    /// </summary>
    private static ToolchainRecipe FromManifest(Toolchains.ToolchainEntry entry)
    {
        var root = Toolchains.ToolchainPaths.SandboxInstallDir(entry.Id);
        return new ToolchainRecipe(
            Id: entry.Id,
            Summary: entry.Summary,
            InstallSteps: ImmutableArray<string>.Empty,
            PathEntries: entry.ResolvedPathEntries(root, PackageCachePolicy.SandboxMount).ToImmutableArray(),
            Environment: entry.Environment
                .Select(kv => new KeyValuePair<string, string>(
                    kv.Key, Toolchains.ToolchainEntry.Expand(kv.Value, root, PackageCachePolicy.SandboxMount)))
                .ToImmutableArray(),
            Probe: entry.ProbeCommand(root, PackageCachePolicy.SandboxMount).ToImmutableArray(),
            BuildEgressHosts: ImmutableArray<string>.Empty,
            Delivery: ToolchainDelivery.RuntimeMount);
    }

    /// <summary>
    /// Every catalogued toolchain, keyed by id — image-layer recipes and user-installable manifest
    /// entries in ONE table.
    ///
    /// <para>Unifying them here is what keeps the closed-catalog property intact while the INSTALLED SET
    /// becomes a user choice: <see cref="ToolchainDeclarationResolver"/> still resolves a repository's
    /// declaration against this one dictionary, so an id that is not curated is still a typed refusal,
    /// never a passthrough and never a silent skip.</para>
    /// </summary>
    public static readonly ImmutableDictionary<string, ToolchainRecipe> All =
        ImageLayerRecipes
            .Concat(Toolchains.ToolchainManifest.Shipped.Entries.Select(FromManifest))
            .ToImmutableDictionary(r => r.Id, StringComparer.Ordinal);

    /// <summary>The catalogued ids, sorted — what an unknown-id failure lists for the human.</summary>
    public static IReadOnlyList<string> KnownIds { get; } =
        All.Keys.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray();

    /// <summary>The recipe for <paramref name="id"/>, or null when it is not catalogued.</summary>
    public static ToolchainRecipe? TryGet(string id) =>
        id is not null && All.TryGetValue(id, out var recipe) ? recipe : null;
}
