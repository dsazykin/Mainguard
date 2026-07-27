using System;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips</b> (visibly) on a host with no Unix file modes — the
/// MG-17 jail-ownership grant is a POSIX permission fact and has no meaning on Windows.
///
/// <para>Deliberately NOT an early <c>return</c> inside the test body: that is the silent-skip trap
/// (the test reports green while asserting nothing, so a regression on Linux CI would look identical to
/// a pass on a Windows dev box). And deliberately not <c>Assert.Skip</c>/<c>SkipException.ForSkip</c>:
/// this project pins <c>xunit</c> 2.9.3 (v2 core), where the dynamic-skip sentinel is reported as a
/// FAILURE. Setting <see cref="FactAttribute.Skip"/> at discovery time is the pattern the repo already
/// uses (<see cref="RequiresGpgFactAttribute"/>, <see cref="RequiresGitLfsFactAttribute"/>,
/// <see cref="Terminal.RequiresLibvtermFactAttribute"/>).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresUnixFileModesFactAttribute : FactAttribute
{
    public RequiresUnixFileModesFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file modes only — the MG-17 group grant on a jail's bind-mount source is a "
                 + "POSIX permission fact, and the daemon that applies it runs inside MainguardEnv.";
        }
    }
}
