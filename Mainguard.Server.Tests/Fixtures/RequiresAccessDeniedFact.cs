using System;
using System.IO;
using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless this process can actually be DENIED access to a
/// directory it owns — the capability a test needs to make the filesystem fail on purpose.
///
/// <para>Root bypasses mode bits, Windows has no <c>chmod</c>, and a DrvFs / metadata-less mount ignores
/// them, so on those the "make it unreadable" setup silently yields a perfectly readable directory. A
/// test written on top of that would then assert against a path it never took and pass for the wrong
/// reason — the failure mode this repo has caught fifteen times. The probe therefore does the real
/// thing (deny, then read) and skips when the denial did not take.</para>
///
/// <para>Skipping is expressed by setting <see cref="FactAttribute.Skip"/> from the constructor, NOT by
/// throwing: this repo is on xunit 2.9.3 (v2 core), where <c>Assert.Skip</c>/<c>SkipException.ForSkip</c>
/// is not a skip at all and reports as a FAILURE.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresAccessDeniedFactAttribute : FactAttribute
{
    public RequiresAccessDeniedFactAttribute()
    {
        if (!AccessDenialSupport.IsSupported)
        {
            Skip = AccessDenialSupport.SkipReason;
        }
    }
}

/// <summary>Whether this process/filesystem can be denied access to a directory it owns (cached).</summary>
internal static class AccessDenialSupport
{
    private static readonly Lazy<bool> _probe = new(Probe);

    internal static bool IsSupported => _probe.Value;

    internal const string SkipReason =
        "this process cannot be denied access to its own directory (running as root, on Windows, or on a "
        + "filesystem that ignores mode bits), so an I/O failure cannot be provoked for real here.";

    /// <summary>
    /// Denies access to a directory and returns an action that restores it, so a caller can wrap exactly
    /// the reads it wants to fail. Only meaningful when <see cref="IsSupported"/>.
    /// </summary>
    internal static IDisposable Deny(string directory)
    {
        SetMode(directory, UnixFileMode.None);
        return new Restore(directory);
    }

    private const UnixFileMode OwnerFull =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static void SetMode(string directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("mode bits are a Unix concept; the probe skips here.");
        }

        File.SetUnixFileMode(directory, mode);
    }

    private static bool Probe()
    {
        var root = Path.Combine(Path.GetTempPath(), "mainguard-denyprobe-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child");
        try
        {
            Directory.CreateDirectory(child);
            using (Deny(root))
            {
                // The probe is the same call the code under test makes. If it answers, the denial did not
                // take and every test built on it would be measuring nothing.
                _ = File.GetAttributes(child);
            }

            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
        catch (Exception)
        {
            // PlatformNotSupportedException (Windows) and anything else: no usable denial.
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    SetMode(root, OwnerFull);
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }

    private sealed class Restore : IDisposable
    {
        private readonly string _directory;

        internal Restore(string directory) => _directory = directory;

        public void Dispose()
        {
            try
            {
                SetMode(_directory, OwnerFull);
            }
            catch
            {
                // Best effort: the temp tree is disposable either way.
            }
        }
    }
}
