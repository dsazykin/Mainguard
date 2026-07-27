using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The secret-delivery exec on the spawn path had <b>no timeout</b>: a Docker endpoint that accepts an
/// exec attach and then never delivers its streams (Docker Desktop's WSL2 socket proxy does exactly
/// this for exec stdin — reproduced on a live daemon) made <c>SpawnAsync</c> hang forever. No error, no
/// log line, no failure — just a spawn that never returns.
///
/// <para>These tests drive the REAL <c>DockerSandboxEngine.WriteSecretFileAsync</c> against an exec-stdin
/// transport that never completes and never observes its cancellation token — i.e. the actual shape of
/// the wedge, not a cooperative stand-in. Every one of them wraps the call in its own outer wait so that
/// an unbounded implementation FAILS the test in seconds instead of hanging the suite.</para>
///
/// <para>The other half of the property is what the expiry leaves behind. The obvious in-jail shell
/// (<c>cat &gt; "$1"</c> then <c>chown</c>/<c>chmod 0400</c>) is the dangerous one: abandoning the
/// stream closes the socket, <c>cat</c> reads that as a clean EOF, and a TRUNCATED secret ends up at the
/// real path in its final readable mode. So the shell stages to <c>&lt;path&gt;.partial</c>, verifies
/// the byte count and publishes with an atomic <c>mv</c>, the failure path unlinks the STAGING file
/// only, and the destination is never partially written at all.</para>
/// </summary>
public class SandboxSecretWriteTimeoutTests
{
    // Short enough to keep the suite fast; the production default is DefaultSecretWriteTimeout (30s).
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    // The outer wait. If the engine's wait is unbounded this elapses and the test fails HERE, naming the
    // defect — rather than hanging the run, which is not a test result at all.
    private static readonly TimeSpan UnboundedIfExceeded = TimeSpan.FromSeconds(10);

    private const string ContainerId = "jail-under-test";
    private const string SecretPath = "/run/mainguard-creds/agent.env";

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("SUPER-SECRET-VALUE");

    private static DockerSandboxEngine EngineOver(FakeDockerClient docker, FakeStdinTransport stdin) =>
        new(docker, new SandboxEngineOptions(
            "mainguard-agents", "http://mainguard-egress-proxy:8888", SecretWriteTimeout: Budget), stdin);

    private static async Task<Exception> AwaitBoundedAsync(Task call)
    {
        var finished = await Task.WhenAny(call, Task.Delay(UnboundedIfExceeded)).ConfigureAwait(false);
        Assert.True(ReferenceEquals(finished, call),
            $"the secret write did not return within {UnboundedIfExceeded.TotalSeconds}s against a Docker "
            + "endpoint that never completes the exec — the wait is unbounded, which is the defect: a "
            + "spawn against such an endpoint never returns at all.");

        return await Record.ExceptionAsync(() => call).ConfigureAwait(false)
               ?? throw new Xunit.Sdk.XunitException("the secret write completed successfully against a wedged endpoint");
    }

    [Fact]
    public async Task SecretWrite_AgainstAnEndpointThatNeverCompletesTheExec_FailsWithinTheBudget()
    {
        var docker = new FakeDockerClient();
        var stdin = new FakeStdinTransport();
        var call = EngineOver(docker, stdin).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, CancellationToken.None);

        var ex = Assert.IsType<SandboxExecTimeoutException>(await AwaitBoundedAsync(call));
        Assert.Equal(ContainerId, ex.ContainerId);
        Assert.Equal(Budget, ex.Timeout);
    }

    [Fact]
    public async Task SecretWriteTimeout_SaysWhatTimedOut_AgainstWhichContainer_AndAfterHowLong()
    {
        // The diagnostic IS the fix as much as the bound is: the failure this replaces produced no
        // output whatsoever, so an operator had nothing to go on.
        var docker = new FakeDockerClient();
        var call = EngineOver(docker, new FakeStdinTransport()).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, CancellationToken.None);

        var ex = Assert.IsType<SandboxExecTimeoutException>(await AwaitBoundedAsync(call));

        Assert.Contains("secret-file write", ex.Message, StringComparison.Ordinal);  // what
        Assert.Contains(SecretPath, ex.Message, StringComparison.Ordinal);           // which file
        Assert.Contains(ContainerId, ex.Message, StringComparison.Ordinal);          // which container
        Assert.Contains("0.3s", ex.Message, StringComparison.Ordinal);               // after how long
    }

    [Fact]
    public async Task SecretWriteTimeout_UnlinksTheHalfWrittenFile_BeforeItThrows()
    {
        var docker = new FakeDockerClient();
        var stdin = new FakeStdinTransport();
        var call = EngineOver(docker, stdin).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, CancellationToken.None);

        var ex = Assert.IsType<SandboxExecTimeoutException>(await AwaitBoundedAsync(call));

        // A cleanup exec ran, and it does NOT itself use stdin — the failure being cleaned up after IS
        // stdin delivery, so a cleanup that needed stdin would be guaranteed to hang too. It therefore
        // goes through the plain Docker.DotNet exec (which works), never through the stdin transport.
        var cleanup = Assert.Single(docker.Exec.Created);
        Assert.False(cleanup.AttachStdin);
        Assert.Contains("rm -f", string.Join(" ", cleanup.Cmd), StringComparison.Ordinal);
        Assert.Equal(ContainerId, cleanup.ContainerId);
        Assert.Single(stdin.Ran); // the write, and nothing else

        // It removes the STAGING file and never the destination. On the CLI-restore path the
        // destination can be the live jail's own fresher credential file, protected by write-if-absent;
        // a cleanup that removed it would destroy a working login while tidying up after a failure that
        // never reached the file at all.
        Assert.Contains(SecretPath + ".partial", cleanup.Cmd);
        Assert.DoesNotContain(SecretPath, cleanup.Cmd);

        // And the caller is told where things stand without having to go and check.
        Assert.Contains("unlinked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_IsReportedAsCancellation_NotAsAWedgedEndpoint()
    {
        // "The daemon is shutting down" and "Docker is not delivering the exec" are different facts with
        // different remediations; reporting the first as the second would send an operator hunting a
        // Docker problem that does not exist. The half-written file is still unlinked either way.
        var docker = new FakeDockerClient();
        using var cts = new CancellationTokenSource();
        var call = EngineOver(docker, new FakeStdinTransport()).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, cts.Token);
        cts.Cancel();

        var ex = await AwaitBoundedAsync(call);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<SandboxExecTimeoutException>(ex);
        Assert.Contains(docker.Exec.Created, e => !e.AttachStdin && e.Cmd.Contains(SecretPath + ".partial"));
    }

    [Fact]
    public async Task SecretWriteThatSucceeds_LeavesNoCleanupBehind()
    {
        // Non-vacuity in the other direction: a "fix" that unlinked the secret on the happy path would
        // pass every test above while making every spawn produce an empty credential file.
        var docker = new FakeDockerClient();
        var stdin = new FakeStdinTransport { Hang = false };
        await EngineOver(docker, stdin).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, CancellationToken.None);

        Assert.Single(stdin.Ran);        // exactly one stdin exec: the write
        Assert.Empty(docker.Exec.Created); // and no cleanup exec followed it
    }

    [Fact]
    public async Task SecretContent_TravelsOnStdinOnly_NeverInArgv()
    {
        // G-13. An exec argument is visible in the jail's own process table for the life of the call, so
        // a "simplification" that passed the credential file as `printf '%s' "$2"` would hand every
        // secret to the agent it is being hidden from. Asserted on the REQUEST the engine builds, which
        // is the only place the choice is made.
        var stdin = new FakeStdinTransport { Hang = false };
        await EngineOver(new FakeDockerClient(), stdin).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, CancellationToken.None);

        var sent = Assert.Single(stdin.Ran);
        Assert.Equal(Secret, sent.Stdin);
        var argv = string.Join(" ", sent.Command);
        Assert.DoesNotContain("SUPER-SECRET-VALUE", argv, StringComparison.Ordinal);
        // The path/uid/length ARE argv-safe and must still be there — otherwise the assertion above
        // would also pass against a command that carried nothing at all.
        Assert.Contains(SecretPath, argv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretWriteShell_StagesAndRenames_SoTheDestinationIsNeverPartiallyWritten()
    {
        // The timeout alone is not enough. `cat > "$1"` treats the abandoned socket as a clean EOF, so
        // the shell used to run on and leave a TRUNCATED secret at the real path — then chown+chmod 0400
        // it, producing something readable and indistinguishable from a good credential file. Reading an
        // exact byte count into a staging path and renaming only a verified-complete file removes that
        // state entirely rather than racing a cleanup against it.
        var stdin = new FakeStdinTransport { Hang = false };
        await EngineOver(new FakeDockerClient(), stdin).WriteSecretFileAsync(
            ContainerId, SecretPath, Secret, 1000, CancellationToken.None);

        var shell = string.Join(" ", Assert.Single(stdin.Ran).Command);
        Assert.Contains("head -c", shell, StringComparison.Ordinal);      // self-terminating, not to EOF
        Assert.Contains("$1.partial", shell, StringComparison.Ordinal);   // staged
        Assert.Contains("wc -c", shell, StringComparison.Ordinal);        // size verified
        Assert.Contains("mv \"$1.partial\" \"$1\"", shell, StringComparison.Ordinal); // atomic publish
        Assert.DoesNotContain("cat > \"$1\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretWrite_WhenTheInJailShellFails_ThrowsInsteadOfReportingSuccess()
    {
        // The exec's exit status was never read at all, so a short write, a failed chown or a full
        // tmpfs produced a jail with no credentials and no error — the same "looks applied, is not"
        // shape as the bug next door.
        var docker = new FakeDockerClient();
        var stdin = new FakeStdinTransport { Hang = false, ExitCode = 75 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EngineOver(docker, stdin).WriteSecretFileAsync(
                ContainerId, SecretPath, Secret, 1000, CancellationToken.None));

        Assert.Contains("75", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SecretPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains(docker.Exec.Created, e => !e.AttachStdin && e.Cmd.Contains(SecretPath + ".partial"));
    }

    /// <summary>
    /// The wedge, reproduced: an exec-stdin delivery that never completes AND never observes its
    /// cancellation token. A fake that honoured the token would only prove that cancellation propagates
    /// — the real endpoint offers no such cooperation, which is why the bound cannot be built on it.
    /// </summary>
    private sealed class FakeStdinTransport : IExecStdinTransport
    {
        public List<ExecStdinRequest> Ran { get; } = new();

        public bool Hang { get; set; } = true;

        /// <summary>What the in-jail shell reports (75 = short read, 74 = could not create/chown).</summary>
        public int ExitCode { get; set; }

        public Task<ExecStdinResult> RunAsync(ExecStdinRequest request, CancellationToken ct)
        {
            Ran.Add(request);
            return Hang
                ? new TaskCompletionSource<ExecStdinResult>().Task
                : Task.FromResult(new ExecStdinResult(ExitCode, string.Empty));
        }
    }

    /// <summary>One recorded exec-create call (the cleanup path, which never uses stdin).</summary>
    private sealed record RecordedExec(string ContainerId, IList<string> Cmd, bool AttachStdin);

    private sealed class FakeExecOperations : IExecOperations
    {
        public List<RecordedExec> Created { get; } = new();

        public Task<ContainerExecCreateResponse> ExecCreateContainerAsync(
            string id, ContainerExecCreateParameters parameters, CancellationToken cancellationToken = default)
        {
            Created.Add(new RecordedExec(id, parameters.Cmd, parameters.AttachStdin));
            return Task.FromResult(new ContainerExecCreateResponse { ID = "exec-" + Created.Count });
        }

        public Task<MultiplexedStream> StartAndAttachContainerExecAsync(
            string id, bool tty, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MultiplexedStream(new ClosableMemoryStream(), multiplexed: false));

        public Task<MultiplexedStream> StartWithConfigContainerExecAsync(
            string id, ContainerExecStartParameters eConfig, CancellationToken cancellationToken = default) =>
            StartAndAttachContainerExecAsync(id, false, cancellationToken);

        public Task StartContainerExecAsync(string id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ContainerExecInspectResponse> InspectContainerExecAsync(
            string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContainerExecInspectResponse { ExitCode = 0 });

        public Task ResizeContainerExecTtyAsync(
            string id, ContainerResizeParameters parameters, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>A write-sink that reads as immediate EOF — an exec that produced no output, which is
    /// what a successful cleanup looks like on the wire.</summary>
    private sealed class ClosableMemoryStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    /// <summary>Only <c>Exec</c> is real — nothing else on this path touches the client.</summary>
    private sealed class FakeDockerClient : IDockerClient
    {
        public FakeExecOperations Exec { get; } = new();

        IExecOperations IDockerClient.Exec => Exec;

        public DockerClientConfiguration Configuration => throw new NotSupportedException();

        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(100);

        public IContainerOperations Containers => throw new NotSupportedException();

        public IImageOperations Images => throw new NotSupportedException();

        public INetworkOperations Networks => throw new NotSupportedException();

        public IVolumeOperations Volumes => throw new NotSupportedException();

        public ISecretsOperations Secrets => throw new NotSupportedException();

        public IConfigOperations Configs => throw new NotSupportedException();

        public ISwarmOperations Swarm => throw new NotSupportedException();

        public ITasksOperations Tasks => throw new NotSupportedException();

        public ISystemOperations System => throw new NotSupportedException();

        public IPluginOperations Plugin => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
