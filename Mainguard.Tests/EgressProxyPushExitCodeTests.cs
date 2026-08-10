using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The egress config push must FAIL when the proxy says it failed.
///
/// <para><b>The defect.</b> <c>EgressProxyConfigurator.ExecAsync</c> ended at
/// <c>ReadOutputToEndAsync</c> and returned <c>void</c> — <c>InspectContainerExecAsync</c> was never
/// called, so the exit code was never fetched. Everything that configures the proxy runs through it: the
/// four rendered policy artefacts and the reload that loads them. Three verdicts in a row were thrown
/// away — <c>reload.sh</c> wrote <c>stale</c>/<c>failed</c> into <c>/run/mainguard/*.status</c> and
/// exited 0 anyway, it printed "dnsmasq FAILED to start" with a log tail onto a stream that was read and
/// dropped, and the daemon never looked at the exit code that would have carried either. Nothing in the
/// daemon reads those status files; only the Docker tests do.</para>
///
/// <para>Because <c>PushConfigAsync</c> is the last act of <c>EnsureReadyAsync</c>, which runs on EVERY
/// spawn and returned success unconditionally, the visible outcome was: a host the user had just removed
/// from the allowlist stays reachable, or every jail's only resolver is dead — and the daemon reports
/// the proxy ready and attaches the next jail to it.</para>
///
/// <para><b>Both halves had to change or either masks the other</b>, and they are tested on separate
/// legs. This file is the daemon half: given a proxy whose command exits non-zero, the push must throw.
/// The <c>reload.sh</c> half (exit non-zero on <c>stale</c>/<c>failed</c> rather than 0) needs a real
/// container and is asserted by the <c>RequiresDocker</c> egress suite.</para>
///
/// <para>The fix already existed on the neighbouring path: <c>WriteFileOverStdinAsync</c> checks
/// <c>result.ExitCode != 0</c> and its comment reads "The exit status was never read here either". That
/// branch is documented as reachable only above 64 KiB, and rendered policy is ~1 KiB, so all four
/// artefacts went through the UNCHECKED path in production.</para>
/// </summary>
public class EgressProxyPushExitCodeTests
{
    private const string ProxyId = "mainguard-egress-proxy";

    private static EgressProxyConfigurator Build(FakeDockerClient docker) =>
        new(docker, EgressAllowlist.WithDefaults(new InMemoryAuditLog()));

    /// <summary>
    /// The reload is the last command the push runs. A proxy that could not load the new policy —
    /// <c>reload.sh</c>'s <c>stale</c> (an unkillable predecessor still serving the PREVIOUS allowlist)
    /// or <c>failed</c> (a daemon that did not come back) — must fail the push, not be discarded.
    /// </summary>
    [Fact]
    public async Task AReloadThatFails_FailsThePush_RatherThanBeingDiscarded()
    {
        // Exit non-zero for the reload only, so the four artefact writes succeed and the reload is
        // unambiguously the step under test.
        var docker = new FakeDockerClient
        {
            ExitCodeFor = cmd => cmd.Contains("/etc/mainguard/reload.sh") ? 1 : 0,
            OutputFor = _ => "[mainguard-egress-proxy] FAILED: dnsmasq did not come back up",
        };

        var ex = await Assert.ThrowsAsync<EgressProxyExecFailedException>(
            () => Build(docker).PushConfigAsync(ProxyId, CancellationToken.None));

        Assert.Equal(1, ex.ExitCode);
        Assert.Equal(ProxyId, ex.ContainerId);

        // The reason the container printed is carried through. This is the stream the old code read to
        // EOF and then dropped, which is why "dnsmasq FAILED to start" never reached anybody.
        Assert.Contains("dnsmasq did not come back up", ex.Output, StringComparison.Ordinal);
        Assert.Contains("reload.sh", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same contract on the four ARTEFACT writes. They take the argv branch of <c>WriteFileAsync</c>
    /// (rendered policy is ~1 KiB against a 64 KiB threshold), which was the unchecked one — so a
    /// default-deny filter that failed to write left the proxy permitting NOTHING, silently.
    /// </summary>
    [Fact]
    public async Task AnArtefactWriteThatFails_FailsThePush_BeforeTheReloadIsEvenAttempted()
    {
        var docker = new FakeDockerClient
        {
            ExitCodeFor = cmd => cmd.Contains("tinyproxy-filter") ? 2 : 0,
            OutputFor = _ => "sh: cannot create /run/mainguard/tinyproxy-filter: Read-only file system",
        };

        var ex = await Assert.ThrowsAsync<EgressProxyExecFailedException>(
            () => Build(docker).PushConfigAsync(ProxyId, CancellationToken.None));

        Assert.Equal(2, ex.ExitCode);

        // It failed AT the bad write: the reload never ran, so a broken policy is never loaded.
        Assert.DoesNotContain(docker.Commands, c => c.Contains("/etc/mainguard/reload.sh"));
    }

    /// <summary>
    /// Non-vacuity, and the regression that matters most: a push whose every command succeeds must still
    /// complete. A "fix" that threw on any output, or that mistook the best-effort resolv.conf read for a
    /// failure, would break every spawn on every healthy daemon.
    /// </summary>
    [Fact]
    public async Task APushWhereEveryCommandSucceeds_StillCompletes()
    {
        var docker = new FakeDockerClient { ExitCodeFor = _ => 0 };

        await Build(docker).PushConfigAsync(ProxyId, CancellationToken.None);

        // All four artefacts, and then the reload — in that order.
        foreach (var artefact in new[]
                 {
                     "tinyproxy-filter", "tinyproxy-upstreams", "dnsmasq.conf", "backstop.sh",
                 })
        {
            Assert.Contains(docker.Commands, c => c.Contains(artefact));
        }

        Assert.Contains(docker.Commands, c => c.Contains("/etc/mainguard/reload.sh"));
    }

    /// <summary>
    /// The one exec that must stay UNCHECKED. Reading the proxy's own <c>/etc/resolv.conf</c> is
    /// documented best-effort and degrades to <see cref="EgressProxyConfig.DockerEmbeddedResolver"/> —
    /// the address that file names in every topology this proxy is created in. Turning it into a hard
    /// failure would make an unreadable file a total spawn outage, which is strictly worse than falling
    /// back to the value we would have written anyway.
    /// </summary>
    [Fact]
    public async Task AFailedResolvConfRead_DoesNotFailThePush_BecauseItIsBestEffortByDesign()
    {
        var docker = new FakeDockerClient { ExitCodeFor = cmd => cmd.Contains("/etc/resolv.conf") ? 1 : 0 };

        await Build(docker).PushConfigAsync(ProxyId, CancellationToken.None);

        Assert.Contains(docker.Commands, c => c.Contains("/etc/resolv.conf"));
        Assert.Contains(docker.Commands, c => c.Contains("/etc/mainguard/reload.sh"));
    }

    /// <summary>
    /// An exec that never RAN is a disturbance, not a policy failure — and this distinction is the whole
    /// reason reading the exit code is not simply "throw on non-zero".
    ///
    /// <para>The proxy is a shared singleton `EnsureReadyAsync` does not own. An exec against it while it
    /// is being stopped or removed reports <c>128</c> / <c>OCI runtime exec failed … error executing
    /// setns process</c> — the command was never delivered, so no verdict about the policy exists. That
    /// is the documented `IsProxyDisturbed` class (container removed / SIGTERM'd / not running
    /// mid-adoption), whose comment warns that fixing the manifestations one at a time does not
    /// converge: "Recognising the CLASS is what stops the fourth." Reading the exit code introduced
    /// exactly that fourth variant, and CI caught it —
    /// `SandboxEgressDockerTests.EnsureReady_WhenTheProxyIsStoppedUnderneathIt_RecoversInsteadOfFailing`
    /// went from passing to a hard failure, because a routine race was being reported as a broken
    /// policy. It must raise the disturbance so the existing whole-sequence retry answers it.</para>
    /// </summary>
    [Fact]
    public async Task AnExecThatNeverRan_IsADisturbance_NotAPolicyFailure()
    {
        // Verbatim from the CI failure, exit code included.
        var docker = new FakeDockerClient
        {
            ExitCodeFor = _ => 128,
            OutputFor = _ => "OCI runtime exec failed: exec failed: unable to start container process: "
                             + "error executing setns process: exit status 1",
        };

        var ex = await Record.ExceptionAsync(
            () => Build(docker).PushConfigAsync(ProxyId, CancellationToken.None));

        Assert.IsNotType<EgressProxyExecFailedException>(ex);
        Assert.True(
            EgressProxyConfigurator.IsProxyDisturbed(ex!),
            $"a stopped-underneath-us proxy must stay retryable, but the push raised {ex?.GetType().Name}");
    }

    /// <summary>A signalled command (128+N — SIGTERM/SIGKILL/SIGINT) is the same story: something
    /// stopped it, which for a shared singleton is a race rather than a verdict.</summary>
    [Theory]
    [InlineData(143)] // SIGTERM — docker stop
    [InlineData(137)] // SIGKILL — docker kill / force-remove
    [InlineData(130)] // SIGINT
    public async Task ASignalledExec_IsADisturbance_NotAPolicyFailure(long exitCode)
    {
        var docker = new FakeDockerClient { ExitCodeFor = _ => exitCode };

        var ex = await Record.ExceptionAsync(
            () => Build(docker).PushConfigAsync(ProxyId, CancellationToken.None));

        Assert.True(EgressProxyConfigurator.IsProxyDisturbed(ex!));
    }

    /// <summary>
    /// The boundary that keeps the two apart: <c>reload.sh</c>'s own failure exit is <c>1</c>, so the
    /// disturbance classification above must not swallow the verdict this change exists to surface.
    /// </summary>
    [Fact]
    public async Task AReloadsOwnFailureExit_IsNotMistakenForADisturbance()
    {
        var docker = new FakeDockerClient
        {
            ExitCodeFor = cmd => cmd.Contains("/etc/mainguard/reload.sh") ? 1 : 0,
        };

        var ex = await Assert.ThrowsAsync<EgressProxyExecFailedException>(
            () => Build(docker).PushConfigAsync(ProxyId, CancellationToken.None));

        Assert.False(
            EgressProxyConfigurator.IsProxyDisturbed(ex),
            "a failed reload must NOT be retried into silence — re-running it produces the same refusal");
    }

    // =============================================================================================
    // A Docker endpoint whose execs report whatever this test says they report.
    // =============================================================================================

    private sealed class FakeDockerClient : IDockerClient
    {
        public FakeDockerClient() => Exec = new FakeExecOperations(this);

        /// <summary>Exit code per command line (the argv joined with spaces).</summary>
        public Func<string, long> ExitCodeFor { get; init; } = _ => 0;

        /// <summary>What the command prints — the diagnosis the old code discarded.</summary>
        public Func<string, string> OutputFor { get; init; } = _ => string.Empty;

        /// <summary>Every command the push ran, in order.</summary>
        public List<string> Commands { get; } = new();

        public FakeExecOperations Exec { get; }

        IExecOperations IDockerClient.Exec => Exec;

        public IContainerOperations Containers { get; } = new FakeContainerOperations();

        public DockerClientConfiguration Configuration => throw new NotSupportedException();

        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(100);

        public IImageOperations Images => throw new NotSupportedException();

        public INetworkOperations Networks => throw new NotSupportedException();

        public IVolumeOperations Volumes => throw new NotSupportedException();

        public ISecretsOperations Secrets => throw new NotSupportedException();

        public IConfigOperations Configs => throw new NotSupportedException();

        public ISwarmOperations Swarm => throw new NotSupportedException();

        public ITasksOperations Tasks => throw new NotSupportedException();

        public ISystemOperations System => throw new NotSupportedException();

        public IPluginOperations Plugin => throw new NotSupportedException();

        public void Dispose() { }

        internal sealed class FakeExecOperations : IExecOperations
        {
            private readonly FakeDockerClient _owner;
            private readonly Dictionary<string, string> _byExecId = new(StringComparer.Ordinal);

            public FakeExecOperations(FakeDockerClient owner) => _owner = owner;

            public Task<ContainerExecCreateResponse> ExecCreateContainerAsync(
                string id, ContainerExecCreateParameters parameters, CancellationToken cancellationToken = default)
            {
                var line = string.Join(" ", parameters.Cmd);
                _owner.Commands.Add(line);
                var execId = "exec-" + _owner.Commands.Count;
                _byExecId[execId] = line;
                return Task.FromResult(new ContainerExecCreateResponse { ID = execId });
            }

            public Task<MultiplexedStream> StartAndAttachContainerExecAsync(
                string id, bool tty, CancellationToken cancellationToken = default) =>
                Task.FromResult(new MultiplexedStream(
                    new ReadOnceStream(_owner.OutputFor(_byExecId[id])), multiplexed: false));

            public Task<MultiplexedStream> StartWithConfigContainerExecAsync(
                string id, ContainerExecStartParameters eConfig, CancellationToken cancellationToken = default) =>
                StartAndAttachContainerExecAsync(id, false, cancellationToken);

            public Task StartContainerExecAsync(string id, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<ContainerExecInspectResponse> InspectContainerExecAsync(
                string id, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ContainerExecInspectResponse
                {
                    ExitCode = _owner.ExitCodeFor(_byExecId[id]),
                });

            public Task ResizeContainerExecTtyAsync(
                string id, ContainerResizeParameters parameters, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        /// <summary>The push inspects the proxy for its per-segment addresses. "Not found" is a state
        /// <c>TryInspectAsync</c> already handles (it degrades to no address), so it keeps this fake to
        /// the exec surface these tests are actually about.</summary>
        private sealed class FakeContainerOperations : IContainerOperations
        {
            public Task<ContainerInspectResponse> InspectContainerAsync(
                string id, CancellationToken cancellationToken = default) =>
                throw new DockerContainerNotFoundException(global::System.Net.HttpStatusCode.NotFound, id);

            // Nothing else on this path touches the client.
            public Task<IList<ContainerListResponse>> ListContainersAsync(
                ContainersListParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<CreateContainerResponse> CreateContainerAsync(
                CreateContainerParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<ContainerProcessesResponse> ListProcessesAsync(
                string id, ContainerListProcessesParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<Stream> GetContainerLogsAsync(
                string id, ContainerLogsParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task GetContainerLogsAsync(
                string id, ContainerLogsParameters parameters, CancellationToken cancellationToken,
                IProgress<string> progress) => throw new NotSupportedException();

            public Task<MultiplexedStream> GetContainerLogsAsync(
                string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IList<ContainerFileSystemChangeResponse>> InspectChangesAsync(
                string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<Stream> ExportContainerAsync(string id, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<Stream> GetContainerStatsAsync(
                string id, ContainerStatsParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task GetContainerStatsAsync(
                string id, ContainerStatsParameters parameters, IProgress<ContainerStatsResponse> progress,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task ResizeContainerTtyAsync(
                string id, ContainerResizeParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<bool> StartContainerAsync(
                string id, ContainerStartParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<bool> StopContainerAsync(
                string id, ContainerStopParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task RestartContainerAsync(
                string id, ContainerRestartParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task KillContainerAsync(
                string id, ContainerKillParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task PauseContainerAsync(string id, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task UnpauseContainerAsync(string id, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<ContainerWaitResponse> WaitContainerAsync(
                string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task RemoveContainerAsync(
                string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<GetArchiveFromContainerResponse> GetArchiveFromContainerAsync(
                string id, GetArchiveFromContainerParameters parameters, bool statOnly,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task ExtractArchiveToContainerAsync(
                string id, ContainerPathStatParameters parameters, Stream stream,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<ContainersPruneResponse> PruneContainersAsync(
                ContainersPruneParameters? parameters = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<ContainerUpdateResponse> UpdateContainerAsync(
                string id, ContainerUpdateParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task RenameContainerAsync(
                string id, ContainerRenameParameters parameters, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<MultiplexedStream> AttachContainerAsync(
                string id, bool tty, ContainerAttachParameters parameters,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        /// <summary>Serves the command's output once, then EOF — an exec that printed something and
        /// exited, which is exactly the shape the old code drained and discarded.</summary>
        private sealed class ReadOnceStream : Stream
        {
            private readonly byte[] _payload;
            private int _position;

            public ReadOnceStream(string payload) =>
                _payload = global::System.Text.Encoding.UTF8.GetBytes(payload ?? string.Empty);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _payload.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _payload.Length - _position;
                if (remaining <= 0)
                {
                    return 0;
                }

                var n = Math.Min(remaining, count);
                Array.Copy(_payload, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
