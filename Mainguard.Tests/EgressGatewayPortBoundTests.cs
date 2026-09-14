using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// F26 — allowlisting the model gateway's HOST must not hand a jail every TCP port on the daemon host.
///
/// <para>tinyproxy's <c>Filter</c> matches a destination hostname and has no notion of a port
/// (<c>ConnectPort</c> bounds only CONNECT), so the MG-4 allowlist entry that makes the gateway
/// reachable also made <c>curl -x $HTTP_PROXY http://&lt;gateway&gt;:11434/</c> reach a local Ollama, a
/// dev server, or Docker's TCP API. The proxy's own netns is where that can be bounded, and these pin
/// the rendered rules.</para>
///
/// <para>F25 — and the proxy is created with the <c>host.docker.internal</c> mapping that a
/// loopback-bound gateway is reached through, since the bind is no longer a LAN-facing address.</para>
/// </summary>
public class EgressGatewayPortBoundTests
{
    private const string ProxyAddress = "172.20.0.2";

    [Fact]
    public void Backstop_BoundsTheGatewayRouteToTheGatewayPort()
    {
        var script = EgressProxyConfig.RenderIptablesScript(
            EgressProxyConfigurator.ProxyPort,
            new[] { ProxyAddress },
            gatewayHostPort: EgressProxyConfigurator.GatewayHostAlias + ":5251");

        // The address is resolved at apply time — the gateway is a name Docker put in /etc/hosts.
        Assert.Contains("getent ahostsv4 " + EgressProxyConfigurator.GatewayHostAlias, script, StringComparison.Ordinal);

        // Exactly one port to the gateway host, DNS aside, and everything else to it is dropped.
        Assert.Contains("-A OUTPUT -d $mg_gw_addr -p tcp --dport 5251 -j ACCEPT", script, StringComparison.Ordinal);
        Assert.Contains("-A OUTPUT -d $mg_gw_addr -j DROP", script, StringComparison.Ordinal);

        // The port a jail would reach for Ollama / a dev server / Docker's API is not admitted.
        Assert.DoesNotContain("--dport 11434", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--dport 2375", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Backstop_WithNoGateway_ConstrainsNothingOutbound()
    {
        var script = EgressProxyConfig.RenderIptablesScript(
            EgressProxyConfigurator.ProxyPort, new[] { ProxyAddress }, gatewayHostPort: null);

        Assert.Contains("mg_gateway_rules=\"\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("getent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-A OUTPUT", script, StringComparison.Ordinal);

        // OUTPUT still has to be DECLARED, or restoring the table would reset it (MG-36's argument).
        Assert.Contains(":OUTPUT ACCEPT", script, StringComparison.Ordinal);
    }

    [Theory]
    // The endpoint is interpolated into a shell line that runs as root inside the proxy. Anything that
    // is not a validated host plus a real port renders no rules at all.
    [InlineData("evil.com;rm -rf /:5251")]
    [InlineData("$(id):5251")]
    [InlineData("a|.*:5251")]
    [InlineData("host.docker.internal:0")]
    [InlineData("host.docker.internal:99999")]
    [InlineData("host.docker.internal:notaport")]
    [InlineData("host.docker.internal")]
    public void Backstop_RefusesToInterpolateAHostileEndpoint(string endpoint)
    {
        var script = EgressProxyConfig.RenderIptablesScript(
            EgressProxyConfigurator.ProxyPort, new[] { ProxyAddress }, endpoint);

        Assert.DoesNotContain("getent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-A OUTPUT", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rendered backstop is executed by <c>sh</c> inside the proxy, and a syntax error there is a
    /// silent whole-fleet egress failure (reload.sh runs it, and the chain stays whatever it was). The
    /// heredoc / pipeline restructuring F26 needed is exactly the kind of change that breaks it, so the
    /// script is parsed by a real shell wherever one exists.
    /// </summary>
    [Fact]
    public void Backstop_IsValidShell()
    {
        if (!File.Exists("/bin/sh"))
        {
            return; // no POSIX shell on this host (Windows dev box) — the Docker tier covers it.
        }

        var script = EgressProxyConfig.RenderIptablesScript(
            EgressProxyConfigurator.ProxyPort,
            new[] { ProxyAddress },
            EgressProxyConfigurator.GatewayHostAlias + ":5251");

        var path = Path.Combine(Path.GetTempPath(), "mainguard-backstop-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, script);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-n " + path)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            process.WaitForExit(30_000);
            Assert.True(process.ExitCode == 0, "sh -n rejected the rendered backstop:\n" + process.StandardError.ReadToEnd());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Proxy_IsCreatedWithTheGatewayHostAlias()
    {
        // F25: the model gateway's default bind is now loopback on Docker Desktop, which a container can
        // only reach through this mapping. Without it CanProxyReachAsync answers false and every BYOK
        // spawn silently skips confinement — i.e. hands the jail the raw provider key.
        var hostConfig = EgressProxyConfigurator.ProxyHostConfig();

        Assert.Contains(
            EgressProxyConfigurator.GatewayHostAlias + ":host-gateway",
            hostConfig.ExtraHosts);
    }
}
