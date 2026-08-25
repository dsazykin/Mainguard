using System;
using System.IO;
using System.Threading;

namespace Mainguard.App.Shell;

/// <summary>
/// The cross-platform single-instance guard behind <see cref="ShellEntryPoint.RunDesktop"/>.
/// Windows keeps the named <see cref="Mutex"/> (proven there; abandoned automatically on any
/// exit). On Unix a named mutex does not actually exclude across processes on macOS — two
/// instances both "won" on-device — so the guard is an exclusive lock on a file under the data
/// root instead: <see cref="FileShare.None"/> maps to an advisory flock, which a second process
/// cannot take and which the kernel releases the moment the holder dies, so a crash never
/// wedges the next launch. The file itself is never deleted; only the lock matters.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly IDisposable _hold;

    private SingleInstanceGuard(IDisposable hold) => _hold = hold;

    /// <summary>The guard, or null when another Mainguard instance already holds it.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(initiallyOwned: true, "Mainguard.App.SingleInstance", out bool isOnlyInstance);
            if (isOnlyInstance) return new SingleInstanceGuard(mutex);
            mutex.Dispose();
            return null;
        }

        var dataRoot = Mainguard.Git.MainguardPaths.DataRoot();
        Directory.CreateDirectory(dataRoot);
        var lockPath = Path.Combine(dataRoot, "app.lock");
        try
        {
            return new SingleInstanceGuard(new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _hold.Dispose();
}
