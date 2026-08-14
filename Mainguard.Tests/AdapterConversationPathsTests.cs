using System;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The <c>conversationPaths</c> / <c>resumeArgs</c> declaration, and above all the ONE invariant that
/// makes a conversation store safe to ship: a declared conversation path may never contain — or be
/// contained by — a declared credential path.
///
/// <para>The store is daemon-owned ext4 that deliberately OUTLIVES the jail, so a credential landing in
/// it is a token persisted to plain disk and remounted into every later jail for that agent id, breaking
/// the standing rule that logins live only in the host OS keychain. The specific accident is a manifest
/// that declares <c>.claude</c>: that is where the transcripts live, and it also contains
/// <c>.claude/.credentials.json</c>. An equality-only check passes that case, which is why every test
/// here is about CONTAINMENT.</para>
/// </summary>
public class AdapterConversationPathsTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Manifest(string body) => $$"""{ "adapters": [ {{body}} ] }""";

    private static string Adapter(string extra) => $$"""
    { "id": "claude-code", "displayName": "Claude Code", "version": "1.2.3", "provenance": "none",
      "sha256": "{{Sha}}", "installCmd": ["true"],
      "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" },
      "launch": ["/opt/mainguard/adapters/bin/claude"],
      {{extra}} }
    """;

    // ---- The declaration ---------------------------------------------------------------------------

    [Fact]
    public void ConversationPaths_AndResumeArgs_ParseAndAreReadable()
    {
        var a = Assert.Single(AdapterManifest.Parse(Manifest(Adapter(
            """
            "credentialPaths": [".claude/.credentials.json"],
            "conversationPaths": [".claude/projects"],
            "resumeArgs": ["--continue"]
            """))).Adapters);

        Assert.Equal(new[] { ".claude/projects" }, a.ConversationPaths);
        Assert.Equal(new[] { "--continue" }, a.ResumeArgs);
    }

    [Fact]
    public void NoConversationPaths_IsAcceptedAsAnHonestEmptyDeclaration()
        // "This CLI gets no persistence yet" must remain expressible. The alternative — requiring the
        // field — is what produces guessed paths, and a wrong path silently persists nothing while
        // looking configured.
        => Assert.Null(Assert.Single(AdapterManifest.Parse(Manifest(Adapter(
            """
            "credentialPaths": [".codex/auth.json"]
            """))).Adapters).ConversationPaths);

    [Fact]
    public void AnAbsoluteConversationPath_IsRefused()
        // Every manifest-sourced in-jail path goes through ONE shape gate; these become
        // /home/agent/<path> as a bind-mount target, so an absolute path would escape $HOME entirely.
        => Assert.Equal(AdapterManifestError.Malformed, Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(Manifest(Adapter(
                """
                "conversationPaths": ["/etc/shadow"]
                """)))).Error);

    [Fact]
    public void AConversationPathThatEscapesHome_IsRefused()
        => Assert.Equal(AdapterManifestError.Malformed, Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(Manifest(Adapter(
                """
                "conversationPaths": ["../../mainguard/repos"]
                """)))).Error);

    // ---- The invariant: containment, in both directions ---------------------------------------------

    [Fact]
    public void DeclaringDotClaude_IsRefused_BecauseItContainsTheCredentialsFile()
    {
        // THE case. `.claude` is where the transcripts live, and it also holds `.credentials.json`
        // (mode 600). A manifest edit that reached for the obvious directory must not be able to route
        // a token into a tree that survives the jail.
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(Adapter(
            """
            "credentialPaths": [".claude/.credentials.json", ".claude.json"],
            "conversationPaths": [".claude"]
            """))));

        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
        Assert.Contains(".claude/.credentials.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConversationPathEqualToACredentialPath_IsRefused()
        => Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(Adapter(
            """
            "credentialPaths": [".claude/.credentials.json"],
            "conversationPaths": [".claude/.credentials.json"]
            """))));

    [Fact]
    public void AConversationPathINSIDEACredentialPath_IsRefusedToo()
        // The other direction, which a one-way containment check would miss: here the CREDENTIAL is the
        // ancestor. A store under a credential directory is still a store that can hold the credential.
        => Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(Adapter(
            """
            "credentialPaths": [".claude"],
            "conversationPaths": [".claude/projects"]
            """))));

    [Fact]
    public void ASiblingNameSharingAPrefix_IsNotAnOverlap()
        // Segment-wise, not substring: `.claude-backup` is genuinely not inside `.claude`, and refusing
        // it would be a false positive that pushes a maintainer toward disabling the check.
        => Assert.Equal(
            new[] { ".claude-backup/projects" },
            Assert.Single(AdapterManifest.Parse(Manifest(Adapter(
                """
                "credentialPaths": [".claude/.credentials.json"],
                "conversationPaths": [".claude-backup/projects"]
                """))).Adapters).ConversationPaths);

    [Fact]
    public void TheOverlapRule_IsTheSameImplementationTheSpawnPathUses()
    {
        // The manifest parser and the spawn path (which reads an install MARKER, not this file) must ask
        // the same question of the same code, or a marker written by an older build could carry a
        // declaration the manifest would have refused.
        Assert.True(ConversationStorePolicy.Overlaps(".claude", ".claude/.credentials.json"));
        Assert.True(ConversationStorePolicy.Overlaps(".claude/.credentials.json", ".claude"));
        Assert.True(ConversationStorePolicy.Overlaps(".claude/projects", ".claude/projects"));
        Assert.False(ConversationStorePolicy.Overlaps(".claude/projects", ".claude/.credentials.json"));
        Assert.False(ConversationStorePolicy.Overlaps(".claude-backup", ".claude"));
    }

    [Fact]
    public void TheSpawnSidePolicy_ThrowsTypedOnAnOverlappingMarker()
    {
        // Not an AdapterManifestException: at spawn time nobody is parsing a manifest. This is the typed
        // SPAWN failure the design calls for — the container is never created.
        var ex = Assert.Throws<ConversationStoreOverlapException>(() =>
            ConversationStorePolicy.UsablePaths(
                "claude-code",
                conversationPaths: new[] { ".claude" },
                credentialPaths: new[] { ".claude/.credentials.json" }));

        Assert.Equal("claude-code", ex.AdapterId);
        Assert.Equal(".claude", ex.ConversationPath);
        Assert.Equal(".claude/.credentials.json", ex.CredentialPath);
    }

    [Fact]
    public void NestedConversationPaths_AreRefused()
        // Two stores where one contains the other would mount a bind mount inside another bind mount —
        // an ordering question nobody should have to reason about, and always a mistake anyway.
        => Assert.Throws<ConversationStoreException>(() =>
            ConversationStorePolicy.UsablePaths(
                "x", new[] { ".claude", ".claude/projects" }, credentialPaths: null));

    // ---- resumeArgs --------------------------------------------------------------------------------

    [Fact]
    public void ResumeArgs_WithoutConversationPaths_IsRefused()
    {
        // A resume flag with no persisted transcripts resumes an empty history on every spawn — the
        // feature would look wired and do nothing, which is the shape this codebase keeps finding.
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(Adapter(
            """
            "resumeArgs": ["--continue"]
            """))));
        Assert.Contains("nothing for the CLI to resume", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankResumeArg_IsRefused()
        => Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(Adapter(
            """
            "conversationPaths": [".claude/projects"],
            "resumeArgs": [" "]
            """))));

    // ---- The shipped starter channel ----------------------------------------------------------------

    [Fact]
    public void TheBundledManifest_DeclaresProjectsForClaudeCode_AndNeverDotClaude()
    {
        var claude = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson())
            .Adapters.Single(a => a.Id == "claude-code");

        Assert.Equal(new[] { ".claude/projects" }, claude.ConversationPaths);
        Assert.Equal(new[] { "--continue" }, claude.ResumeArgs);
        // Restated as its own assertion rather than left implied by the parse: this is the line somebody
        // would "helpfully" widen while debugging a missing transcript.
        Assert.DoesNotContain(".claude", claude.ConversationPaths!, StringComparer.Ordinal);
    }

    [Fact]
    public void TheBundledManifest_DeclaresNothingForTheCLIsNobodyVerified()
    {
        // Honest emptiness, asserted. codex/gemini-cli/qwen-code/opencode each keep session state
        // somewhere, but nobody has verified WHERE on the pinned versions — and a guessed path would
        // persist nothing while looking configured, which is strictly worse than declaring nothing.
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
        foreach (var id in new[] { "codex", "gemini-cli", "qwen-code", "opencode" })
        {
            var adapter = manifest.Adapters.Single(a => a.Id == id);
            Assert.True(adapter.ConversationPaths is null or { Count: 0 },
                $"'{id}' declares conversationPaths; verify them against the pinned version first.");
        }
    }

    [Fact]
    public void EveryBundledAdapter_KeepsItsConversationPathsClearOfItsCredentials()
        // The whole-manifest sweep, so adding a CLI cannot land the overlap in a row nobody re-read.
        // (Parse already enforces it; this states the property under its own name, at the level of the
        // shipped file rather than of a synthetic fixture.)
        => Assert.All(
            AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters,
            a => Assert.Empty(ConversationStorePolicy.FindCredentialOverlaps(
                a.ConversationPaths, a.CredentialPaths)));
}
