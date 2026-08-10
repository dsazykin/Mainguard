using System;
using System.Globalization;
using System.Threading.Tasks;
using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// Attempts the memory-inspection vector <b>for real</b> inside a live jail — <c>process_vm_readv</c>
/// and <c>ptrace</c>, through libc — and reports each call's return value and errno.
///
/// <para><b>Why this exists (tickets #59/#60).</b> The only runtime evidence for G2 control 1 used to be
/// <c>cat /proc/1/mem &gt;/dev/null 2&gt;&amp;1; echo $?</c> with a non-zero exit required. That assertion
/// could not fail: <c>cat</c> on ANY <c>/proc/&lt;pid&gt;/mem</c> reads sequentially from offset 0, an
/// address that is never mapped in any process, so it exits non-zero before a single permission check
/// matters. Measured: <c>cat /proc/self/mem</c> — the caller's OWN memory, every check trivially
/// satisfied — also exits 1. It passed under <c>--privileged</c> plus <c>seccomp=unconfined</c> plus
/// <c>CAP_SYS_PTRACE</c>. No test anywhere executed <c>ptrace</c> or <c>process_vm_readv</c> in a live
/// jail, and those three denials are the profile's <i>only</i> delta over stock moby.</para>
///
/// <para><b>Why every call is made twice, once against self.</b> Directed at another process, a refusal
/// is ambiguous: an EPERM from the seccomp filter is indistinguishable from one raised by
/// <c>ptrace_may_access</c> or by Yama's <c>ptrace_scope=1</c> — the default on the Ubuntu runner this
/// executes on — so a cross-process probe alone goes GREEN on a jail with no seccomp profile at all.
/// Directed at <b>self</b>, the kernel's permission check returns early (<c>task == current</c>), Yama
/// does not apply, the address is mapped and the length is right. Exactly one thing is left that can
/// make the call fail, and that is the filter. Without the profile the self-directed call SUCCEEDS.</para>
///
/// <para>Every call is framed by the SCRIPT rather than inferred from an exit code, so a probe that
/// never ran is a MISSING frame — its own distinct failure — instead of an absence that reads as a
/// refusal. That distinction is the entire subject of these tickets.</para>
/// </summary>
internal static class JailSyscallProbe
{
    /// <summary>EPERM — the errno every rule in this profile's deny group returns (<c>errnoRet: 1</c>).</summary>
    public const int Eperm = 1;

    /// <summary>The self-directed <c>process_vm_readv</c>: the attribution anchor.</summary>
    public const string ProcessVmReadvSelf = "PVRSELF";

    /// <summary>The cross-process <c>process_vm_readv</c>: the vector itself.</summary>
    public const string ProcessVmReadvInit = "PVRINIT";

    /// <summary><c>PTRACE_TRACEME</c> — needs no capability and no permission over any other task.</summary>
    public const string PtraceTraceme = "TRACEME";

    /// <summary><c>PTRACE_ATTACH</c> against pid 1.</summary>
    public const string PtraceAttachInit = "ATTACHINIT";

    /// <summary>
    /// The probe. Runs through the jail's own <c>python3</c> (baked into the agent base image's nix
    /// toolchain), because the container has no compiler and these syscalls have no shell equivalent
    /// that is not the very read-semantics trap this replaces.
    ///
    /// <para>The <c>PTRACE_ATTACH</c> arm detaches immediately if it somehow succeeds: an attached tracee
    /// is SIGSTOPped, and leaving the jail's pid 1 stopped would break the container for every later
    /// assertion in the same run.</para>
    /// </summary>
    public const string Script = """
        import ctypes, os, sys

        PTRACE_TRACEME, PTRACE_ATTACH, PTRACE_DETACH = 0, 16, 17

        def emit(name, value):
            sys.stdout.write("%s[%s]" % (name, value))

        libc = ctypes.CDLL(None, use_errno=True)

        class IoVec(ctypes.Structure):
            _fields_ = [("base", ctypes.c_void_p), ("length", ctypes.c_size_t)]

        src = ctypes.create_string_buffer(b"mainguard-oob-key-canary")
        dst = ctypes.create_string_buffer(len(src))
        n = len(src)

        libc.process_vm_readv.restype = ctypes.c_ssize_t
        libc.process_vm_readv.argtypes = [
            ctypes.c_int, ctypes.POINTER(IoVec), ctypes.c_ulong,
            ctypes.POINTER(IoVec), ctypes.c_ulong, ctypes.c_ulong]
        libc.ptrace.restype = ctypes.c_long
        libc.ptrace.argtypes = [ctypes.c_long, ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p]

        def vm_read(pid):
            local = IoVec(ctypes.cast(dst, ctypes.c_void_p).value, n)
            remote = IoVec(ctypes.cast(src, ctypes.c_void_p).value, n)
            ctypes.set_errno(0)
            rc = libc.process_vm_readv(pid, ctypes.byref(local), 1, ctypes.byref(remote), 1, 0)
            return rc, ctypes.get_errno()

        rc, err = vm_read(os.getpid())
        emit("PVRSELF", "rc=%d errno=%d" % (rc, err))

        rc, err = vm_read(1)
        emit("PVRINIT", "rc=%d errno=%d" % (rc, err))

        ctypes.set_errno(0)
        rc = libc.ptrace(PTRACE_TRACEME, 0, None, None)
        emit("TRACEME", "rc=%d errno=%d" % (rc, ctypes.get_errno()))

        ctypes.set_errno(0)
        rc = libc.ptrace(PTRACE_ATTACH, 1, None, None)
        emit("ATTACHINIT", "rc=%d errno=%d" % (rc, ctypes.get_errno()))
        if rc == 0:
            libc.ptrace(PTRACE_DETACH, 1, None, None)

        emit("MGPROBE", "DONE")
        """;

    /// <summary>Runs the probe in <paramref name="containerId"/> and returns its framed stdout. Throws
    /// with the raw output when the probe did not run to completion — "the call was never attempted" is
    /// a different fact from "the call was refused" and must never be read as one.</summary>
    public static async Task<string> RunAsync(
        SandboxFixture fx, string containerId, Action<string>? log = null)
    {
        var result = await fx.ExecAsync(containerId, "python3", "-c", Script).ConfigureAwait(false);

        log?.Invoke($"syscall probe => exit {result.ExitCode}");
        log?.Invoke($"  stdout: {result.Stdout.Trim()}");
        log?.Invoke($"  stderr: {result.Stderr.Trim()}");

        Assert.True(
            result.Stdout.Contains("MGPROBE[DONE]", StringComparison.Ordinal),
            "the in-jail syscall probe did not run to completion, so none of its frames mean anything. "
            + $"exit={result.ExitCode} stdout=<<{result.Stdout}>> stderr=<<{result.Stderr}>>");

        return result.Stdout;
    }

    /// <summary>Reads one <c>NAME[rc=… errno=…]</c> frame.</summary>
    public static (long Rc, int Errno) ReadCall(string output, string frame)
    {
        var value = ReadFrame(output, frame);
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length == 2, $"the '{frame}' frame is malformed: '{value}'");
        Assert.True(
            long.TryParse(parts[0]["rc=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rc),
            $"the '{frame}' frame has no readable return value: '{value}'");
        Assert.True(
            int.TryParse(parts[1]["errno=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var errno),
            $"the '{frame}' frame has no readable errno: '{value}'");
        return (rc, errno);
    }

    /// <summary>Reads one sentinel-framed value. A MISSING frame throws with its own message.</summary>
    public static string ReadFrame(string output, string name)
    {
        var opener = name + "[";
        var start = output.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(
            start >= 0,
            $"the in-jail probe never printed the '{opener}' frame — it did not run, so nothing was proven. "
            + $"Raw output: <<{output}>>");
        start += opener.Length;
        var end = output.IndexOf(']', start);
        Assert.True(end >= 0, $"the '{opener}' frame was never closed. Raw output: <<{output}>>");
        return output[start..end];
    }
}
