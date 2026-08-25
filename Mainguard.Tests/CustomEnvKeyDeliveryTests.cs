using System;
using System.Collections.Generic;
using System.Linq;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The CLIENT leg of BYOK "Custom key" delivery (P2-01 <c>llm_env_*</c>): the settings page stores a
/// custom env-var key in the OS keyring, and every spawn must read it back out and put it on the wire
/// as <c>SpawnAgentRequest.extra_env</c>. The daemon-side legs are pinned elsewhere
/// (<c>SandboxSecretsMappingTests</c> maps <c>extra_env</c> onto the jail's env-file,
/// <c>SecretDeliveryDockerTests</c> proves the file really lands in a real jail); this file pins the
/// hop between them, which nothing covered.
///
/// <para><b>Why it exists (ISSUES-LOG #37).</b> A live-UI walkthrough leg saved a custom key, spawned
/// agents, found <c>/run/secrets/agent/agent.env</c> empty in the jail, and logged BYOK custom keys as
/// silently non-functional. The spawns it measured had been made through a raw <c>SpawnAgent</c> RPC —
/// a setup shortcut from an adjacent leg — so they never went through
/// <see cref="DaemonBackedOrchestrator.CollectCustomEnvKeys"/> and carried <c>extra_env=[]</c> by
/// construction. The product was correct and unprovably so. These tests make the claim checkable
/// without a daemon, a keyring, or a jail.</para>
/// </summary>
public sealed class CustomEnvKeyDeliveryTests
{
    /// <summary>A client pointed at a port nothing listens on: every test here asks what the
    /// orchestrator READS, never what it sends, so the channel is never contacted.</summary>
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    /// <summary>An in-memory stand-in for the OS keyring, exposing the same two seams the orchestrator
    /// takes: enumerate names by prefix, then read one value by name.</summary>
    private static DaemonBackedOrchestrator OrchestratorOver(IReadOnlyDictionary<string, string> keyring) =>
        new(UncontactedClient(),
            ownsClient: true,
            keystoreLookup: name => keyring.TryGetValue(name, out var v) ? v : null,
            keystoreList: prefix => keyring.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray());

    [Fact]
    public void CustomKey_IsReadBackFromTheKeyring_UnderTheBareEnvVarName()
    {
        using var orchestrator = OrchestratorOver(new Dictionary<string, string>
        {
            ["llm_env_OPENROUTER_API_KEY"] = "sk-or-real",
        });

        var extra = orchestrator.CollectCustomEnvKeys();

        // The keystore NAME carries the llm_env_ prefix; the wire must not. An entry named
        // "llm_env_OPENROUTER_API_KEY" inside the jail is exactly the failure #37 thought it saw:
        // present, correct, and invisible to the CLI that reads OPENROUTER_API_KEY.
        Assert.Equal("sk-or-real", Assert.Contains("OPENROUTER_API_KEY", extra));
        Assert.DoesNotContain(extra.Keys, k => k.StartsWith(
            ApiKeyProviderMap.CustomEnvKeyPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryStoredCustomKey_Travels_NotJustTheFirst()
    {
        using var orchestrator = OrchestratorOver(new Dictionary<string, string>
        {
            ["llm_env_OPENROUTER_API_KEY"] = "sk-or-1",
            ["llm_env_TOGETHER_API_KEY"] = "sk-tg-2",
            ["llm_env_A"] = "shortest-legal-name",
        });

        var extra = orchestrator.CollectCustomEnvKeys();

        Assert.Equal(3, extra.Count);
        Assert.Equal("sk-or-1", extra["OPENROUTER_API_KEY"]);
        Assert.Equal("sk-tg-2", extra["TOGETHER_API_KEY"]);
        Assert.Equal("shortest-legal-name", extra["A"]);
    }

    [Fact]
    public void OtherKeyringEntries_AreNeverInjected()
    {
        // The keyring also holds the per-provider BYOK keys (llm_anthropic — those ride modelApiKey,
        // under the adapter's DECLARED variable) and the harvested CLI logins (cli_login_* — those are
        // whole files restored into the jail's $HOME). Injecting either as an environment variable
        // would put a credential somewhere its owner never agreed to put it, so the prefix filter is a
        // boundary and not a convenience.
        using var orchestrator = OrchestratorOver(new Dictionary<string, string>
        {
            ["llm_anthropic"] = "sk-ant-should-not-travel-here",
            ["cli_login_claude-code"] = "{\"files\":[]}",
            ["token_github"] = "ghp_should-not-travel-at-all",
            ["llm_env_OPENROUTER_API_KEY"] = "sk-or-1",
        });

        var extra = orchestrator.CollectCustomEnvKeys();

        Assert.Equal("OPENROUTER_API_KEY", Assert.Single(extra).Key);
    }

    [Fact]
    public void NothingStored_YieldsAnEmptySet_NotNull()
    {
        // The empty case must stay a real (empty) dictionary: the spawn path passes it straight into
        // extraEnv, and null there means "fall back to whatever the daemon cached for this repo" —
        // a different decision than "this user has no custom keys".
        using var orchestrator = OrchestratorOver(new Dictionary<string, string>());

        var extra = orchestrator.CollectCustomEnvKeys();

        Assert.NotNull(extra);
        Assert.Empty(extra);
    }

    [Fact]
    public void AnEmptyOrUnreadableValue_IsSkipped_NotInjectedAsAnEmptyVariable()
    {
        // A keyring entry whose payload cannot be decrypted reads back as null (SecureKeyring swallows
        // the failure), and an empty variable is worse than an absent one: the CLI stops asking for a
        // login and starts failing to authenticate instead.
        using var orchestrator = OrchestratorOver(new Dictionary<string, string>
        {
            ["llm_env_BROKEN_KEY"] = "",
            ["llm_env_GOOD_KEY"] = "sk-good",
        });

        var extra = orchestrator.CollectCustomEnvKeys();

        Assert.Equal("GOOD_KEY", Assert.Single(extra).Key);
    }

    [Fact]
    public void ThePrefixAlone_IsNotAVariableName()
    {
        // "llm_env_" with nothing after it would map to the empty env-var name, which the daemon
        // rejects for the WHOLE spawn (AgentSpawnService validates every name up front) — one
        // malformed keyring file would otherwise make every agent unstartable.
        using var orchestrator = OrchestratorOver(new Dictionary<string, string>
        {
            ["llm_env_"] = "no-name-at-all",
            ["llm_env_GOOD_KEY"] = "sk-good",
        });

        var extra = orchestrator.CollectCustomEnvKeys();

        Assert.Equal("GOOD_KEY", Assert.Single(extra).Key);
    }
}
