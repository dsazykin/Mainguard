using Mainguard.Agents.Agents.Adapters;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// MG-4 — the flip that keeps the real provider key out of the jail.
///
/// <para><c>BuildSecrets</c> wrote the raw BYOK key verbatim into <c>agentEnv</c>, which lands in
/// <c>/run/secrets/agent.env</c> — agent-uid-owned mode <c>0400</c>, i.e. readable by the agent. With
/// a gateway available and a CLI that declares a base-URL variable, the jail instead receives an
/// opaque <c>mg_sess_</c> token and is pointed at the gateway; the gateway swaps in the real key
/// daemon-side at the network hop.</para>
///
/// <para><b>Both conditions are required, and the default is unchanged behaviour.</b> No gateway
/// configured, or a CLI that cannot be redirected, means the real key still goes in — handing a token
/// to a CLI that talks straight to the provider would simply break it. This is why the confinement is
/// opt-in rather than a silent behavioural change.</para>
/// </summary>
public sealed class BuildSecretsConfinementTests
{
    private const string RealKey = "sk-ant-REAL-PROVIDER-KEY";
    private const string Token = "mg_sess_deadbeef";
    private const string GatewayUrl = "http://172.17.0.1:5251";

    private static InstalledAdapterMarker Adapter(string? baseUrlEnvVar) =>
        new("claude-code", "2.1.0", new[] { "claude" },
            ApiKeyEnvVar: "ANTHROPIC_API_KEY",
            EgressHosts: null,
            CredentialPaths: null,
            BaseUrlEnvVar: baseUrlEnvVar);

    // The MG-4 fix: the key is replaced, not merely accompanied.
    [Fact]
    public void WithGatewayAndBaseUrlVar_JailGetsTheTokenAndTheGatewayUrl_NeverTheRealKey()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(
            RealKey, Adapter("ANTHROPIC_BASE_URL"),
            gateway: new GatewayConfinement(GatewayUrl, Token));

        Assert.Equal(Token, secrets.AgentEnv["ANTHROPIC_API_KEY"]);
        Assert.Equal(GatewayUrl, secrets.AgentEnv["ANTHROPIC_BASE_URL"]);

        // The real key appears nowhere in anything the jail receives.
        Assert.DoesNotContain(RealKey, string.Join("|", secrets.AgentEnv.Values));
    }

    // Default posture (no gateway configured) must be byte-for-byte today's behaviour.
    [Fact]
    public void WithoutGateway_RealKeyStillGoesIn_UnchangedBehaviour()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(RealKey, Adapter("ANTHROPIC_BASE_URL"));

        Assert.Equal(RealKey, secrets.AgentEnv["ANTHROPIC_API_KEY"]);
        Assert.False(secrets.AgentEnv.ContainsKey("ANTHROPIC_BASE_URL"));
    }

    // A CLI with no base-URL variable cannot be redirected — giving it a token would break it, so the
    // real key must still go in even though a gateway exists.
    [Fact]
    public void GatewayButNoBaseUrlVar_RealKeyStillGoesIn_RatherThanBreakingTheCli()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(
            RealKey, Adapter(baseUrlEnvVar: null),
            gateway: new GatewayConfinement(GatewayUrl, Token));

        Assert.Equal(RealKey, secrets.AgentEnv["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("ANTHROPIC_BASE_URL", secrets.AgentEnv.Keys);
    }

    // The OAuth path: no key was supplied at all, so there is nothing to confine and nothing to
    // inject. The CLI signs in interactively and its token files ride credentialPaths instead.
    [Fact]
    public void OAuthCli_NoKeySupplied_NothingIsInjected_EvenWithAGateway()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(
            modelApiKey: null, Adapter("ANTHROPIC_BASE_URL"),
            gateway: new GatewayConfinement(GatewayUrl, Token));

        Assert.False(secrets.AgentEnv.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(secrets.AgentEnv.ContainsKey("ANTHROPIC_BASE_URL"));
    }

    // The user's llm_env_* entries must survive the flip untouched.
    [Fact]
    public void ExtraEnv_SurvivesTheFlip()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets(
            RealKey, Adapter("ANTHROPIC_BASE_URL"),
            extraEnv: new System.Collections.Generic.Dictionary<string, string> { ["LLM_ENV_FOO"] = "bar" },
            cliCredentials: null,
            gateway: new GatewayConfinement(GatewayUrl, Token));

        Assert.Equal("bar", secrets.AgentEnv["LLM_ENV_FOO"]);
        Assert.Equal(Token, secrets.AgentEnv["ANTHROPIC_API_KEY"]);
    }
}
