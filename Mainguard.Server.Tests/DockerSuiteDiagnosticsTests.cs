using System;
using System.IO;
using System.Text.Json;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;

namespace Mainguard.Server.Tests;

/// <summary>
/// The RequiresDocker suite's sweep is destructive on purpose, and everything it destroys carries
/// production's literal names (<c>mainguard-egress-proxy</c>, <c>mainguard-agents</c>, the
/// <c>mainguard-agent-</c> segments) because PR #278 established those names cannot be per-run — MG-7's
/// resolver pin and MG-18's posture gate key on them. What therefore separates "correct test cleanup"
/// from "the owner's jails just lost their networks" is nothing but WHICH dockerd it ran against, and on
/// a Windows workstation those are two different daemons whose containers are indistinguishable by name:
/// the app's engine lives inside the <c>MainguardEnv</c> WSL distro (where <c>mainguardd</c> runs), the
/// suite's is whatever engine the shell running <c>dotnet test</c> sees.
///
/// <para>On 2026-08-07 a swept TEST jail — up, <c>Networks</c> empty, on Docker Desktop — was read as the
/// owner's destroyed jail by three readers in a row, while the owner's actual jail sat untouched and
/// attached on the other engine. The damage was a wrong diagnosis, not a wrong deletion. These tests
/// cover the three things that make that diagnosis cheap next time: a refusal when the suite CAN prove it
/// is on the app's own engine, an engine named on every destructive line, and a durable record of what
/// was removed — none of which needs a Docker daemon to assert.</para>
/// </summary>
public sealed class DockerSuiteDiagnosticsTests
{
    private static readonly DockerEngineIdentity AppsOwnEngine = new(
        Endpoint: "unix:///var/run/docker.sock",
        Name: "Daniels-G-14",
        ServerVersion: "20.10.24+dfsg1",
        OperatingSystem: "Debian GNU/Linux 12 (bookworm)",
        DockerHost: null);

    // ---- the refusal ----------------------------------------------------------------------------

    /// <summary>
    /// Inside Mainguard OS the local socket IS the engine the owner's jails run on, so the run stops —
    /// and the message has to carry the whole diagnosis, because a refusal that only says "refusing"
    /// trades one unexplained symptom for another. Four separate facts are required of it: which engine,
    /// what proved it, what would have been destroyed, and what to do instead.
    /// </summary>
    [Fact]
    public void SweepGuard_RefusesInsideMainguardOs_NamingEngineEvidenceDamageAndRemedy()
    {
        var refusal = DockerSuiteSweepGuard.RefusalFor(insideMainguardOs: true, AppsOwnEngine);

        Assert.NotNull(refusal);

        // Which engine — the fact that distinguishes this from a routine test sweep.
        Assert.Contains("Daniels-G-14", refusal);
        Assert.Contains("unix:///var/run/docker.sock", refusal);

        // What proved it.
        Assert.Contains(MainguardOsHost.MarkerPath, refusal);

        // What would have been destroyed, by name.
        Assert.Contains(EgressProxyConfigurator.ProxyContainerName, refusal);
        Assert.Contains(EgressProxyConfigurator.AgentNetworkName, refusal);
        Assert.Contains(EgressProxyConfigurator.EgressNetworkName, refusal);

        // Why it cannot simply be renamed away (the #278 reasoning a reader will otherwise re-litigate).
        Assert.Contains("#278", refusal);

        // What to do instead.
        Assert.Contains("Run the suite from outside Mainguard OS", refusal);
    }

    /// <summary>
    /// The other direction, and the one that matters most in practice: on a disposable engine the guard
    /// must stay out of the way. A guard that fired unconditionally would take CI's entire sandbox
    /// security leg down, which is a far more expensive failure than the one it protects against.
    /// </summary>
    [Fact]
    public void SweepGuard_AllowsTheSweep_WhenNotInsideMainguardOs()
    {
        var testEngine = new DockerEngineIdentity(
            "unix:///var/run/docker.sock", "docker-desktop", "29.4.3", "Docker Desktop", DockerHost: null);

        Assert.Null(DockerSuiteSweepGuard.RefusalFor(insideMainguardOs: false, testEngine));
        Assert.Null(DockerSuiteSweepGuard.RefusalFor(insideMainguardOs: false, AppsOwnEngine));
    }

    /// <summary>
    /// The detector reads the file system, both ways. Asserted against a real file rather than the real
    /// path, because a probe that can only be observed in its "false" state is indistinguishable from one
    /// hard-wired to false — and on every machine this suite runs on today, false is the honest answer.
    /// </summary>
    [Fact]
    public void MainguardOsMarker_IsReadOffTheFileSystem_InBothDirections()
    {
        var marker = Path.Combine(Path.GetTempPath(), "mainguardos-release-" + Guid.NewGuid().ToString("N"));

        Assert.False(MainguardOsHost.IsInside(marker));

        File.WriteAllText(marker, "MAINGUARDOS_VERSION=test\n");
        try
        {
            Assert.True(MainguardOsHost.IsInside(marker));
        }
        finally
        {
            File.Delete(marker);
        }

        Assert.False(MainguardOsHost.IsInside(marker));

        // And the real marker is the payload's release stamp, not a name invented here — the same file
        // the VM upgrade path reads.
        Assert.Equal(Mainguard.Agents.Agents.Bootstrap.VmUpgradeCommands.InstalledReleaseStampPath, MainguardOsHost.MarkerPath);
    }

    // ---- naming the engine ----------------------------------------------------------------------

    /// <summary>
    /// The description always carries the endpoint, including when <c>docker info</c> could not be read:
    /// "unknown engine" without an endpoint would leave exactly the ambiguity this exists to remove.
    /// <c>DOCKER_HOST</c> is surfaced too — it is the one way a run reaches a daemon that is not the local
    /// socket, and the suite cannot tell from the far side whose daemon that is.
    /// </summary>
    [Fact]
    public void EngineDescription_AlwaysNamesTheEndpoint_AndAnyDockerHostOverride()
    {
        Assert.Contains("docker-desktop", new DockerEngineIdentity(
            "unix:///var/run/docker.sock", "docker-desktop", "29.4.3", "Docker Desktop", null).Describe());
        Assert.Contains("29.4.3", new DockerEngineIdentity(
            "unix:///var/run/docker.sock", "docker-desktop", "29.4.3", "Docker Desktop", null).Describe());

        var unknown = DockerEngineIdentity.Unknown("tcp://10.0.0.5:2375", "HttpRequestException");
        Assert.Contains("tcp://10.0.0.5:2375", unknown.Describe());
        Assert.Contains("HttpRequestException", unknown.Describe());

        var remote = new DockerEngineIdentity(
            "tcp://10.0.0.5:2375", "far-away", "26.0.0", "Ubuntu", DockerHost: "tcp://10.0.0.5:2375");
        Assert.Contains("DOCKER_HOST=tcp://10.0.0.5:2375", remote.Describe());
    }

    // ---- the record of what was removed -----------------------------------------------------------

    /// <summary>
    /// The sweep report names every resource it took out and every container it evicted to get there.
    /// That list is the only thing on the box that answers "what detached my container from its
    /// networks?" — the container itself keeps no evidence, which is precisely why the answer had to be
    /// guessed at last time.
    /// </summary>
    [Fact]
    public void SweepReport_NamesEveryRemovedResource_AndEveryEvictedContainer()
    {
        var outcome = new SweepOutcome(
            EgressProxyConfigurator.ProxyContainerName,
            ProxyRemoved: true,
            new[]
            {
                new SweptNetwork("mainguard-agents", "a1b2c3d4e5f67890", Array.Empty<string>()),
                new SweptNetwork("mainguard-agent-9af04ef88eb6-7badbb8d32e2", "0123456789abcdef",
                    new[] { "mainguard-9af04ef88eb6-7badbb8d32e2", EgressProxyConfigurator.ProxyContainerName }),
            });

        var report = outcome.Describe(new DockerEngineIdentity(
            "unix:///var/run/docker.sock", "docker-desktop", "29.4.3", "Docker Desktop", null));

        Assert.Contains("docker-desktop", report);                                  // on which engine
        Assert.Contains("removed container " + EgressProxyConfigurator.ProxyContainerName, report);
        Assert.Contains("mainguard-agents", report);                                // which networks
        Assert.Contains("mainguard-agent-9af04ef88eb6-7badbb8d32e2", report);
        Assert.Contains("evicted mainguard-9af04ef88eb6-7badbb8d32e2", report);      // whose endpoints
        Assert.Contains("2 network(s) deleted", report);
    }

    /// <summary>
    /// A sweep that removed nothing says so in words. "Swept" with no list is the shape that lets a
    /// destructive step and a no-op read the same, which is half of how the wrong engine got blamed.
    /// </summary>
    [Fact]
    public void SweepReport_SaysExplicitlyWhenItRemovedNothing()
    {
        var report = SweepOutcome.Nothing(EgressProxyConfigurator.ProxyContainerName)
            .Describe(DockerEngineIdentity.Unknown("unix:///var/run/docker.sock", "Timeout"));

        Assert.Contains("nothing to remove", report);
        Assert.Contains("unix:///var/run/docker.sock", report);
        Assert.DoesNotContain("network(s) deleted", report);
    }

    /// <summary>
    /// The journal is what survives the run: it appends (a later run must not erase the record that
    /// explains an earlier one), it stamps each line, and it never throws — a diagnostic that can fail a
    /// run would be a worse defect than the one it documents.
    /// </summary>
    [Fact]
    public void Journal_AppendsStampedLines_AndSwallowsAnUnwritablePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "mainguard-suite-journal-" + Guid.NewGuid().ToString("N") + ".log");
        var journal = new DockerSuiteJournal(path);
        Assert.Equal(path, journal.Path);

        try
        {
            journal.Record("entry sweep swept docker-desktop — removed container mainguard-egress-proxy");
            journal.Record("exit sweep swept docker-desktop — nothing to remove");

            var text = File.ReadAllText(path);
            Assert.Contains("entry sweep", text);
            Assert.Contains("exit sweep", text);                       // appended, not overwritten
            Assert.Contains("mainguard-egress-proxy", text);
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }

        // A directory is never a writable file: the record is lost, the run is not.
        new DockerSuiteJournal(Path.GetTempPath()).Record("this cannot be written anywhere");
    }

    /// <summary>The stamp carries the instant and the process, so two interleaved runs on one box stay
    /// tellable apart in the same file.</summary>
    [Fact]
    public void JournalStamp_CarriesTheUtcInstantAndThePid()
    {
        var line = DockerSuiteJournal.Stamp(
            "entry sweep", at: new DateTimeOffset(2026, 8, 7, 20, 58, 41, TimeSpan.Zero), pid: 4242);

        Assert.Equal("2026-08-07T20:58:41Z pid 4242 [docker-suite] entry sweep", line);
    }

    // ---- the channel the announcement travels on --------------------------------------------------

    /// <summary>
    /// The fixture announces the engine through an xunit diagnostic message, and that channel only exists
    /// while <c>xunit.runner.json</c> says so AND the file is copied next to the test assembly.
    ///
    /// <para>Measured on .NET 10 with xunit 2.9.3 + xunit.runner.visualstudio 3.1.4: a fixture's
    /// <c>Console</c> output reaches <c>dotnet test --verbosity normal</c> only, while an
    /// <c>IMessageSink</c> diagnostic reaches the DEFAULT verbosity too — and vanishes entirely when the
    /// flag is false. Both halves of that are silent failures (nobody notices a line that stopped being
    /// printed), so both are asserted here rather than trusted.</para>
    /// </summary>
    [Fact]
    public void RunnerConfig_KeepsDiagnosticMessagesOn_NextToTheTestAssembly()
    {
        var config = Path.Combine(AppContext.BaseDirectory, "xunit.runner.json");
        Assert.True(File.Exists(config),
            $"xunit.runner.json is not next to the test assembly ({config}); without it the RequiresDocker "
            + "fixture's engine announcement is dropped at the default `dotnet test` verbosity.");

        using var json = JsonDocument.Parse(File.ReadAllText(config));
        Assert.True(json.RootElement.TryGetProperty("diagnosticMessages", out var flag),
            "xunit.runner.json no longer sets diagnosticMessages.");
        Assert.True(flag.GetBoolean(),
            "diagnosticMessages is off, so the RequiresDocker fixture's 'which engine am I sweeping' line "
            + "is invisible to a plain `dotnet test` — the exact silence that made a swept TEST jail look "
            + "like the owner's destroyed one.");
    }
}
