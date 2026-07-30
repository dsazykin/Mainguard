using System;
using System.IO;
using Mainguard.Git;
using Mainguard.Git.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The teeth behind <see cref="TestDataRootIsolation"/>: proves this run is NOT pointed at the
/// developer's real <c>~/.mainguard</c> (<c>%LocalAppData%\Mainguard</c> on Windows).
///
/// <para>Without isolation the suite shared one SQLite file, one <c>config.json</c> and one keyring with
/// the developer's actual installation — tests could be broken by real user state and could corrupt it.
/// Both halves were real: an orphaned <c>__EFMigrationsLock</c> row in that shared database timed out every
/// headless harness, and the row itself was most likely left there BY a killed test run. That is invisible
/// while it works, so it needs a test that fails the moment the redirect stops happening rather than a
/// comment asking people to remember.</para>
///
/// <para>To confirm these assertions have teeth, delete the <c>[ModuleInitializer]</c> body in
/// <see cref="TestDataRootIsolation"/> and run this class: every fact here goes red.</para>
/// </summary>
public class DataRootIsolationTests
{
    [Fact]
    public void Data_root_under_test_is_not_the_real_user_data_root() => AssertIsolated();

    /// <summary>
    /// The isolation assertion itself, factored out so anything that is about to TOUCH the data root can
    /// check first. Without that ordering a broken redirect would hang the suite instead of failing it:
    /// migrating the real database is exactly the operation that blocks forever on an orphaned
    /// <c>__EFMigrationsLock</c> row (see <see cref="MigrationLockTests"/>), and a hang reports nothing.
    /// </summary>
    private static void AssertIsolated()
    {
        var real = MainguardPaths.RealUserDataRoot();
        var underTest = MainguardPaths.DataRoot();

        Assert.True(Path.IsPathRooted(underTest), $"Data root must be absolute; got '{underTest}'.");
        Assert.False(
            PathsEqual(underTest, real),
            $"The test run resolved Mainguard's data root to the REAL user root ('{real}'). Tests must "
            + "never read or write the developer's own database, settings or keyring — see "
            + "TestDataRootIsolation.");
        Assert.False(
            IsUnder(underTest, real),
            $"The test data root '{underTest}' sits INSIDE the real user root '{real}'. A subdirectory is "
            + "still the user's directory: it survives the run, and its SQLite lock is the same lock.");
    }

    [Fact]
    public void Data_root_under_test_comes_from_the_relocation_seam()
    {
        var configured = Environment.GetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{MainguardPaths.DataRootOverrideVariable} is unset, so MainguardPaths.DataRoot() falls back "
            + "to the real per-user root. TestDataRootIsolation's module initializer must set it.");
        Assert.Equal(Path.GetFullPath(configured!.Trim()), MainguardPaths.DataRoot());
    }

    /// <summary>
    /// The consumers that actually caused the damage, checked end-to-end rather than by inspecting the
    /// path: the default <see cref="AppDbContext"/> (which <c>App.Initialize</c> migrates on every headless
    /// session) and <see cref="SettingsService"/> (the user's real preferences file) must both land inside
    /// the isolated root — and the migration must now complete, which against the shared user database was
    /// exactly what timed out.
    /// </summary>
    [Fact]
    public void Default_database_and_settings_land_inside_the_isolated_root()
    {
        AssertIsolated(); // never open the real database, not even to fail
        var root = MainguardPaths.DataRoot();

        using var db = new AppDbContext();
        var dataSource = db.Database.GetDbConnection().DataSource;
        Assert.True(
            IsUnder(dataSource, root),
            $"The default AppDbContext resolved to '{dataSource}', outside the isolated root '{root}'.");

        db.Database.Migrate();
        Assert.True(File.Exists(dataSource), $"Migrations did not produce a database at '{dataSource}'.");

        // SettingsService writes config.json under the data root on save; constructing + saving is the
        // only way to observe where it points, and here that write must stay in the sandbox.
        var settings = new SettingsService();
        settings.Save();
        Assert.True(
            File.Exists(Path.Combine(root, "config.json")),
            $"SettingsService did not write config.json into the isolated root '{root}'.");
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), PathComparison);

    private static bool IsUnder(string candidate, string root)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
            || string.Equals(normalizedCandidate, normalizedRoot, PathComparison);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
