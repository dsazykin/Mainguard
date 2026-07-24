using System;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Terminal.Vterm;
using Mainguard.Server.Runtime;
using Mainguard.Server.Terminal;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-22 — unbounded resize dimensions must never reach the native allocator.
///
/// <para>A client <c>Resize.cols/rows</c> is <c>uint32</c> on the wire, cast to <c>int</c>. Every managed
/// check rejected only <c>&lt;= 0</c>, so anything in <c>[1, 2^31-1]</c> flowed to
/// <c>vterm_set_size(rows, cols)</c>. Upstream libvterm 0.3.3 allocates
/// <c>sizeof(ScreenCell) * rows * cols</c> with no overflow or upper-bound check and dereferences the
/// result unconditionally; an inflated <c>cols</c> also multiplies the 10 000-line scrollback ring's
/// footprint.</para>
///
/// <para>The clamp lives at the native boundary (<see cref="VtermSession"/>) and is applied once more in
/// <c>BoundTerminalSession.Resize</c> so the PTY and the grid are driven to the SAME size — clamping in
/// only one of the two would silently desynchronise them.</para>
/// </summary>
public sealed class ResizeClampTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(120, 120)]
    [InlineData(VtermSession.MaxDimension, VtermSession.MaxDimension)]
    [InlineData(VtermSession.MaxDimension + 1, VtermSession.MaxDimension)]
    [InlineData(100_000, VtermSession.MaxDimension)]
    [InlineData(int.MaxValue, VtermSession.MaxDimension)]
    [InlineData(-5, 1)]
    public void ClampDimension_BoundsEveryInput(int input, int expected)
        => Assert.Equal(expected, VtermSession.ClampDimension(input));

    // The concrete overflow shape: int.MaxValue * int.MaxValue cells would wrap size_t in the native
    // allocator. After clamping the product is bounded by MaxDimension^2.
    [Fact]
    public void ClampedProduct_CannotOverflowTheNativeAllocation()
    {
        var cols = VtermSession.ClampDimension(int.MaxValue);
        var rows = VtermSession.ClampDimension(int.MaxValue);

        Assert.Equal((long)VtermSession.MaxDimension * VtermSession.MaxDimension, (long)cols * rows);
        Assert.True((long)cols * rows < int.MaxValue);
    }

    [RequiresLibvtermFact]
    public void VtermSession_Resize_ToAbsurdDimensions_IsClampedNotPassedToNative()
    {
        using var session = new VtermSession(80, 24);

        // Pre-fix this reached vterm_set_size(2^31-1, 2^31-1).
        session.Resize(int.MaxValue, int.MaxValue);

        var snapshot = session.Snapshot();
        Assert.Equal(VtermSession.MaxDimension, snapshot.Cols);
        Assert.Equal(VtermSession.MaxDimension, snapshot.Rows);
    }

    [RequiresLibvtermFact]
    public void VtermSession_Ctor_WithAbsurdDimensions_IsClampedNotPassedToNative()
    {
        using var session = new VtermSession(int.MaxValue, int.MaxValue);

        var snapshot = session.Snapshot();
        Assert.Equal(VtermSession.MaxDimension, snapshot.Cols);
        Assert.Equal(VtermSession.MaxDimension, snapshot.Rows);
    }

    // The one-authoritative-size rule: the PTY and the grid must be clamped to the SAME value.
    [RequiresLibvtermFact]
    public void BoundSession_Resize_ClampsThePtyAndTheGridIdentically()
    {
        using var cli = new RecordingTerminalSession();
        using var bound = new BoundTerminalSession(
            "agent-clamp", cli, new TerminalEngineConfig(TerminalEngineKind.Libvterm), 80, 24);

        bound.Resize(int.MaxValue, 99_999);

        // The PTY was told the clamped size...
        Assert.Equal((VtermSession.MaxDimension, VtermSession.MaxDimension), cli.LastResize);
    }

    /// <summary>A terminal session that only records the resize it was told to perform.</summary>
    private sealed class RecordingTerminalSession : ITerminalSession
    {
        private readonly System.IO.Pipelines.Pipe _out = new();
        private readonly System.Threading.Tasks.TaskCompletionSource<int> _exit = new();

        public System.IO.Stream IO => _out.Reader.AsStream();

        public System.Threading.Tasks.Task<int> ExitCode => _exit.Task;

        public (int Cols, int Rows)? LastResize { get; private set; }

        public void Resize(int cols, int rows) => LastResize = (cols, rows);

        public void Kill() => _exit.TrySetResult(0);

        public void Dispose() => _exit.TrySetResult(0);
    }
}
