using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>A paused jail is not exec-able, and the log must say that once — not four Docker stack traces.</b>
///
/// <para>The observed defect: a merge whose auto-rebase conflicts leaves the worker <c>docker pause</c>d,
/// and the harvest sweep runs against every agent. <c>docker exec</c> into a frozen container is refused
/// outright by the engine, so each declared credential path produced
/// <c>cli credential harvest failed: kind=claude-code path=.claude/.credentials.json
/// Docker.DotNet.DockerApiException … status code=Conflict</c> — four of them in one session. Nothing was
/// wrong that an operator could act on, and a warning-with-stack that means "as expected" is exactly how
/// the warnings that DO mean something stop being read.</para>
///
/// <para>Both directions are pinned here, because only silencing the noise would be the worse bug: the
/// skip happens for a paused jail, a RUNNING jail whose harvest genuinely fails is still reported loudly
/// with its exception, and a running jail whose harvest succeeds still harvests. A guard that could not
/// tell those apart would be indistinguishable from deleting the catch block.</para>
/// </summary>
public class PausedJailHarvestTests
{
    private const string AgentKind = "probe-cli";
    private const string LoginPath = ".probe/login.json";
    private const string Login = "{\"token\":\"abc\"}";
    private const string ContainerId = "ctr-frozen";

    private static readonly AdapterSettingsPath SettingsEntry = new("home", ".probe/settings.json");

    /// <summary>
    /// The credential harvest asks whether the jail is frozen BEFORE it execs, so a paused container
    /// costs no exec, yields no exception and produces no warning — and the fact is still stated once.
    /// </summary>
    [Fact]
    public async Task CredentialHarvest_OfAPausedJail_ExecsNothing_AndSurfacesNoDockerFailure()
    {
        using var registry = TempRegistry.WithCredentialPath(AgentKind, LoginPath);
        var engine = new ProbeEngine { Paused = true, ExecThrows = DockerRefusedAPausedExec() };
        var log = new CapturingLog();
        var launcher = new SandboxAgentLauncher(
            new EngineOnlyEnvironment(engine), new InstalledAdapterCatalog(registry.Path), log.Factory);

        var harvested = await launcher.HarvestCliCredentialsAsync(ContainerId, AgentKind);

        Assert.Empty(harvested);
        // The assertion that matters: it never reached the engine at all. Catching the Conflict and
        // logging it quietly would satisfy the log assertions below and still spend an exec per file.
        Assert.Empty(engine.Execs);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.DoesNotContain(log.Entries, e => e.Exception is not null);
        var skip = Assert.Single(log.Entries, e => e.Message.Contains("harvest skipped", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, skip.Level);
        // It says nothing was lost — an operator reading this after a conflicted merge must not be left
        // wondering whether the user's login was dropped on the floor.
        Assert.Contains("nothing was lost", skip.Message, StringComparison.Ordinal);

        // The control, on the same launcher: unfrozen, the very same call execs and harvests.
        engine.Paused = false;
        engine.ExecThrows = null;
        engine.Stdout = Convert.ToBase64String(Encoding.UTF8.GetBytes(Login));

        var again = await launcher.HarvestCliCredentialsAsync(ContainerId, AgentKind);

        Assert.Single(engine.Execs);
        Assert.Equal(Login, Encoding.UTF8.GetString(Assert.Single(again).Content));
    }

    /// <summary>
    /// The settings harvest is the credential harvest's twin and had the identical hole — it runs on the
    /// same stop, into the same frozen jail.
    /// </summary>
    [Fact]
    public async Task SettingsHarvest_OfAPausedJail_ExecsNothing_AndSurfacesNoDockerFailure()
    {
        using var registry = TempRegistry.WithSettingsPath(AgentKind, SettingsEntry);
        var engine = new ProbeEngine { Paused = true, ExecThrows = DockerRefusedAPausedExec() };
        var log = new CapturingLog();
        var launcher = new SandboxAgentLauncher(
            new EngineOnlyEnvironment(engine), new InstalledAdapterCatalog(registry.Path), log.Factory);

        var harvested = await launcher.HarvestCliSettingsAsync(ContainerId, AgentKind);

        Assert.Empty(harvested);
        Assert.Empty(engine.Execs);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);

        // Control: unfrozen, it execs.
        engine.Paused = false;
        engine.ExecThrows = null;
        engine.Stdout = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));

        await launcher.HarvestCliSettingsAsync(ContainerId, AgentKind);
        Assert.Single(engine.Execs);
    }

    /// <summary>
    /// <b>The half that must NOT be silenced.</b> A running jail whose harvest really fails is still a
    /// warning, still carries its exception, and still names the file — that error means a login was lost
    /// and an operator has to see it. Without this the fix above could have been "stop logging".
    /// </summary>
    [Fact]
    public async Task AGenuineHarvestFailure_OnALiveJail_IsStillReportedLoudly()
    {
        using var registry = TempRegistry.WithCredentialPath(AgentKind, LoginPath);
        var engine = new ProbeEngine { Paused = false, ExecThrows = new InvalidOperationException("pipe died") };
        var log = new CapturingLog();
        var launcher = new SandboxAgentLauncher(
            new EngineOnlyEnvironment(engine), new InstalledAdapterCatalog(registry.Path), log.Factory);

        var harvested = await launcher.HarvestCliCredentialsAsync(ContainerId, AgentKind);

        Assert.Empty(harvested); // a failed harvest still never blocks the stop
        var failure = Assert.Single(
            log.Entries, e => e.Message.Contains("credential harvest failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.NotNull(failure.Exception);
        Assert.Contains(LoginPath, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An engine that cannot answer "is it paused?" must not be read as "it is paused": that would skip a
    /// harvest that would have worked, and a skipped credential harvest costs the user their login. The
    /// harvest runs, and the engine's own answer is what decides.
    /// </summary>
    [Fact]
    public async Task AnEngineThatCannotAnswerThePauseQuestion_IsNotTreatedAsPaused()
    {
        using var registry = TempRegistry.WithCredentialPath(AgentKind, LoginPath);
        var engine = new ProbeEngine
        {
            PausedThrows = new InvalidOperationException("inspect unavailable"),
            Stdout = Convert.ToBase64String(Encoding.UTF8.GetBytes(Login)),
        };
        var log = new CapturingLog();
        var launcher = new SandboxAgentLauncher(
            new EngineOnlyEnvironment(engine), new InstalledAdapterCatalog(registry.Path), log.Factory);

        var harvested = await launcher.HarvestCliCredentialsAsync(ContainerId, AgentKind);

        Assert.Single(engine.Execs);
        Assert.Equal(Login, Encoding.UTF8.GetString(Assert.Single(harvested).Content));
    }

    /// <summary>What Docker answers an <c>exec</c> against a frozen container — the exception the operator
    /// log was being handed a stack trace of, four times a session.</summary>
    private static Exception DockerRefusedAPausedExec() =>
        new InvalidOperationException(
            "Docker API responded with status code=Conflict, response={\"message\":"
            + "\"Container ctr-frozen is paused, unpause the container before exec\"}");

    // ---- doubles ---------------------------------------------------------------------------------

    /// <summary>A sandbox engine that answers the two questions this path asks, and records the execs.</summary>
    private sealed class ProbeEngine : ISandboxEngine
    {
        private readonly List<IReadOnlyList<string>> _execs = new();

        public bool Paused { get; set; }

        public Exception? PausedThrows { get; set; }

        public Exception? ExecThrows { get; set; }

        public string Stdout { get; set; } = string.Empty;

        public IReadOnlyList<IReadOnlyList<string>> Execs => _execs;

        public Task<bool> IsPausedAsync(string containerId, CancellationToken ct = default) =>
            PausedThrows is not null ? Task.FromException<bool>(PausedThrows) : Task.FromResult(Paused);

        public Task<SandboxExecResult> ExecAsync(
            string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        {
            _execs.Add(command);
            return ExecThrows is not null
                ? Task.FromException<SandboxExecResult>(ExecThrows)
                : Task.FromResult(new SandboxExecResult(0, Stdout, string.Empty));
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("the harvest path never spawns");

        public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The substrate slice the harvest actually uses; everything else refuses to be asked.</summary>
    private sealed class EngineOnlyEnvironment : IAgentEnvironment
    {
        public EngineOnlyEnvironment(ISandboxEngine sandboxes) => Sandboxes = sandboxes;

        public string SubstrateId => "probe";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public ISandboxEngine Sandboxes { get; }

        public IRepoProvisioner Repos =>
            throw new NotSupportedException("the harvest path never provisions a repo");

        public IAgentWorktreeManager Worktrees =>
            throw new NotSupportedException("the harvest path never touches a worktree");

        public IEgressPolicy Egress =>
            throw new NotSupportedException("the harvest path never touches egress");

        public SyncRemote ResolveSyncRemote(string repoHash) =>
            throw new NotSupportedException("the harvest path never resolves a sync remote");
    }

    /// <summary>A temp adapter registry holding ONE install marker — the daemon's only declaration of
    /// what may be harvested out of a jail.</summary>
    private sealed class TempRegistry : IDisposable
    {
        private TempRegistry(string agentKind, InstalledAdapterMarker marker)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mg-harvest-registry-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
            File.WriteAllText(
                System.IO.Path.Combine(Path, agentKind + ".json"), InstalledAdapterMarker.Serialize(marker));
        }

        public string Path { get; }

        public static TempRegistry WithCredentialPath(string agentKind, params string[] credentialPaths) =>
            new(agentKind, new InstalledAdapterMarker(
                agentKind, "1.0.0", new[] { "/bin/true" },
                ApiKeyEnvVar: null, EgressHosts: null, CredentialPaths: credentialPaths));

        public static TempRegistry WithSettingsPath(string agentKind, params AdapterSettingsPath[] settings) =>
            new(agentKind, new InstalledAdapterMarker(
                agentKind, "1.0.0", new[] { "/bin/true" }, SettingsPaths: settings));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* never fail a test from cleanup */ }
        }
    }

    /// <summary>Everything the launcher logged, so a test can assert the LEVEL and the exception rather
    /// than only the text — which is the whole distinction between "as expected" and "act on this".</summary>
    private sealed class CapturingLog : ILoggerProvider
    {
        private readonly List<Entry> _entries = new();

        public CapturingLog() =>
            Factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(this); });

        public ILoggerFactory Factory { get; }

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose() => Factory.Dispose();

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private sealed class Sink : ILogger
        {
            private readonly CapturingLog _owner;

            public Sink(CapturingLog owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_owner._entries)
                {
                    _owner._entries.Add(new Entry(logLevel, formatter(state, exception), exception));
                }
            }
        }
    }
}
