using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Mainguard.Agents.Terminal.Vterm;

/// <summary>
/// P/Invoke surface over the pinned libvterm (0.3.3) — the P2-18 server-side terminal engine.
/// Daemon-side only: the client never calls into this type, and <c>libvterm.so</c> ships only in
/// the daemon payload (built in CI from pinned source, see <c>build/libvterm/</c>). The library
/// resolves via <c>MAINGUARD_LIBVTERM</c> (explicit path), then <c>libvterm.so(.0)</c> next to the
/// daemon binary, then the system library — so tests, the payload, and a dev box all work without
/// code changes. <see cref="IsAvailable"/> probes without throwing so callers (and the P2-04
/// harness) can degrade to the interim engine where the native library is absent (e.g. Windows
/// local-dev, where the libvterm engine is out of scope by design).
///
/// <para>libvterm is NOT thread-safe and <c>vterm_screen_set_callbacks</c> STORES the callback
/// struct pointer rather than copying it — <see cref="VtermSession"/> owns both invariants (one
/// session = one thread; callbacks pinned in unmanaged memory for the session's lifetime).</para>
/// </summary>
internal static partial class VtermNative
{
    private const string Lib = "vterm";

    /// <summary>Marker value in <c>chars[0]</c> for the trailing spacer cell of a wide glyph.</summary>
    internal const uint WideSpacerChar = 0xFFFFFFFF;

    internal const int MaxCharsPerCell = 6;

    // VTermDamageSize
    internal const int DamageScroll = 3;

    // VTermProp
    internal const int PropCursorVisible = 1;
    internal const int PropAltScreen = 3;
    internal const int PropMouse = 8;

    // VTermColor.type: low bit selects RGB(0)/INDEXED(1); flag bits mark the default fg/bg.
    private const byte ColorIndexedBit = 0x01;
    private const byte ColorDefaultFgBit = 0x02;
    private const byte ColorDefaultBgBit = 0x04;

    private static readonly object ResolveGate = new();
    private static bool _resolverInstalled;
    private static bool? _available;

    [StructLayout(LayoutKind.Sequential)]
    internal struct VTermRect
    {
        public int StartRow, EndRow, StartCol, EndCol;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VTermPos
    {
        public int Row, Col;
    }

    /// <summary>
    /// Mirror of <c>VTermScreenCell</c> (layout validated against the pinned 0.3.3 build: 40 bytes;
    /// gcc packs the attr bitfields LSB-first into one 32-bit unit; VTermColor is a 4-byte union
    /// whose unused payload bytes carry garbage and MUST be masked via the type byte).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct VTermScreenCell
    {
        public fixed uint Chars[MaxCharsPerCell];
        public byte Width;
        private byte _pad0, _pad1, _pad2;
        public uint Attrs; // bold:1 underline:2 italic:1 blink:1 reverse:1 conceal:1 strike:1 font:4 dwl:1 dhl:2 small:1 baseline:2
        public uint Fg;    // VTermColor
        public uint Bg;    // VTermColor

        public readonly bool Bold => (Attrs & 0x1) != 0;
        public readonly bool Underline => (Attrs & 0x6) != 0;
        public readonly bool Italic => (Attrs & 0x8) != 0;
        public readonly bool Reverse => (Attrs & 0x20) != 0;
        public readonly bool Strike => (Attrs & 0x80) != 0;
    }

    /// <summary>Decodes a raw <c>VTermColor</c> union into the engine-neutral cell colour.</summary>
    internal static VtermColor DecodeColor(uint raw, bool isForeground)
    {
        var type = (byte)raw;
        if (isForeground ? (type & ColorDefaultFgBit) != 0 : (type & ColorDefaultBgBit) != 0)
        {
            return VtermColor.Default;
        }

        if ((type & ColorIndexedBit) != 0)
        {
            return VtermColor.Indexed((byte)(raw >> 8));
        }

        return VtermColor.Rgb((byte)(raw >> 8), (byte)(raw >> 16), (byte)(raw >> 24));
    }

    /// <summary>Encodes the neutral colour back into a raw <c>VTermColor</c> union (for sb_popline).</summary>
    internal static uint EncodeColor(VtermColor color, bool isForeground)
    {
        return color.Kind switch
        {
            VtermColorKind.Indexed => (uint)(ColorIndexedBit | (color.Index << 8)),
            VtermColorKind.Rgb => (uint)(color.R << 8) | (uint)(color.G << 16) | (uint)(color.B << 24),
            _ => isForeground ? ColorDefaultFgBit : ColorDefaultBgBit,
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DamageFn(VTermRect rect, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int MoveRectFn(VTermRect dest, VTermRect src, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int MoveCursorFn(VTermPos pos, VTermPos oldPos, int visible, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetTermPropFn(int prop, IntPtr value, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int BellFn(IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ResizeFn(int rows, int cols, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SbPushLineFn(int cols, IntPtr cells, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SbPopLineFn(int cols, IntPtr cells, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SbClearFn(IntPtr user);

    /// <summary>Mirror of <c>VTermScreenCallbacks</c>. libvterm keeps the POINTER to this struct;
    /// <see cref="VtermSession"/> pins it in unmanaged memory for its whole lifetime.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VTermScreenCallbacks
    {
        public DamageFn? Damage;
        public MoveRectFn? MoveRect;
        public MoveCursorFn? MoveCursor;
        public SetTermPropFn? SetTermProp;
        public BellFn? Bell;
        public ResizeFn? Resize;
        public SbPushLineFn? SbPushLine;
        public SbPopLineFn? SbPopLine;
        public SbClearFn? SbClear;
    }

    [DllImport(Lib)]
    internal static extern IntPtr vterm_new(int rows, int cols);

    [DllImport(Lib)]
    internal static extern void vterm_free(IntPtr vt);

    [DllImport(Lib)]
    internal static extern void vterm_set_utf8(IntPtr vt, int isUtf8);

    [DllImport(Lib)]
    internal static extern void vterm_set_size(IntPtr vt, int rows, int cols);

    [DllImport(Lib)]
    internal static extern unsafe UIntPtr vterm_input_write(IntPtr vt, byte* bytes, UIntPtr len);

    [DllImport(Lib)]
    internal static extern unsafe UIntPtr vterm_output_read(IntPtr vt, byte* buffer, UIntPtr len);

    [DllImport(Lib)]
    internal static extern void vterm_keyboard_unichar(IntPtr vt, uint c, int mod);

    [DllImport(Lib)]
    internal static extern void vterm_keyboard_key(IntPtr vt, int key, int mod);

    [DllImport(Lib)]
    internal static extern void vterm_keyboard_start_paste(IntPtr vt);

    [DllImport(Lib)]
    internal static extern void vterm_keyboard_end_paste(IntPtr vt);

    [DllImport(Lib)]
    internal static extern IntPtr vterm_obtain_screen(IntPtr vt);

    [DllImport(Lib)]
    internal static extern void vterm_screen_reset(IntPtr screen, int hard);

    [DllImport(Lib)]
    internal static extern void vterm_screen_enable_altscreen(IntPtr screen, int altScreen);

    [DllImport(Lib)]
    internal static extern void vterm_screen_set_damage_merge(IntPtr screen, int size);

    [DllImport(Lib)]
    internal static extern void vterm_screen_flush_damage(IntPtr screen);

    [DllImport(Lib)]
    internal static extern void vterm_screen_set_callbacks(IntPtr screen, IntPtr callbacks, IntPtr user);

    [DllImport(Lib)]
    internal static extern int vterm_screen_get_cell(IntPtr screen, VTermPos pos, out VTermScreenCell cell);

    /// <summary>
    /// Installs the library resolver once. Resolution order: <c>MAINGUARD_LIBVTERM</c> (explicit
    /// file path), <c>libvterm.so(.0)</c> beside the running binary (the daemon payload layout),
    /// then the platform default probe for <c>libvterm.so.0</c> / <c>vterm</c>.
    /// </summary>
    internal static void EnsureResolver()
    {
        lock (ResolveGate)
        {
            if (_resolverInstalled)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(VtermNative).Assembly, static (name, assembly, searchPath) =>
            {
                if (name != Lib)
                {
                    return IntPtr.Zero;
                }

                var explicitPath = Environment.GetEnvironmentVariable("MAINGUARD_LIBVTERM");
                if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)
                    && NativeLibrary.TryLoad(explicitPath, out var fromEnv))
                {
                    return fromEnv;
                }

                // The daemon payload ships the pinned build under the host's native name:
                // .so on Linux (the WSL2 VM), .dylib on the macos-host substrate.
                foreach (var candidate in new[]
                         { "libvterm.so", "libvterm.so.0", "libvterm.dylib", "libvterm.0.dylib" })
                {
                    var beside = Path.Combine(AppContext.BaseDirectory, candidate);
                    if (File.Exists(beside) && NativeLibrary.TryLoad(beside, out var fromBase))
                    {
                        return fromBase;
                    }
                }

                var systemName = OperatingSystem.IsMacOS() ? "libvterm.0.dylib" : "libvterm.so.0";
                if (NativeLibrary.TryLoad(systemName, typeof(VtermNative).Assembly, searchPath, out var system))
                {
                    return system;
                }

                return IntPtr.Zero; // fall through to the default probe for "vterm"
            });

            _resolverInstalled = true;
        }
    }

    /// <summary>
    /// True when libvterm can actually be loaded and driven here. Never throws — the P2-04 harness
    /// and the engine flag use this to fall back to the interim engine where the native library is
    /// absent (Windows local-dev); CI sets <c>MAINGUARD_REQUIRE_LIBVTERM=1</c> and separately
    /// asserts availability so the gate can never silently skip.
    /// </summary>
    internal static bool IsAvailable
    {
        get
        {
            lock (ResolveGate)
            {
                if (_available is { } cached)
                {
                    return cached;
                }
            }

            bool available;
            try
            {
                EnsureResolver();
                var vt = vterm_new(2, 2);
                available = vt != IntPtr.Zero;
                if (available)
                {
                    vterm_free(vt);
                }
            }
            catch (DllNotFoundException)
            {
                available = false;
            }
            catch (EntryPointNotFoundException)
            {
                available = false;
            }

            lock (ResolveGate)
            {
                _available = available;
            }

            return available;
        }
    }
}
