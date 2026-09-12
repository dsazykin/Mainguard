using System;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Mainguard.Server.Cli;
using Mainguard.Server.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// B1/B3 — the daemon must not come up looking healthy while its audit trail goes nowhere.
///
/// <para>The chain was: <see cref="SecureKeyring.SaveSecret"/> refuses the audit master key when the
/// key ring has no at-rest protector → <see cref="AuditCrypto"/>'s constructor propagates it →
/// <c>RegisterAuditLog</c>'s <c>catch (Exception)</c> swallowed it → <see cref="InMemoryAuditLog"/>.
/// The daemon then bound its port, served every RPC, answered <c>VerifyAudit</c> with
/// <c>persistent=false</c>, and lost every audit event at shutdown. The startup smoke passed
/// <i>because</i> the fallback was silent — the same "carries on looking healthy" failure the
/// fail-closed keyring was written to remove, reintroduced one layer up.</para>
///
/// <para>The in-memory journal is still the right answer for a daemon DB that would not open (the
/// alternative there is no daemon at all, and the user sees that immediately). It is never the right
/// answer for a key-ring posture failure, which has a named remedy.</para>
/// </summary>
public sealed class AuditPersistenceBootTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public AuditPersistenceBootTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mg-audit-boot-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "daemon.db");
        using var db = new AppDbContext(_dbPath);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>Puts the daemon's key ring into the state a fresh Linux/WSL box is in: a ring whose
    /// master key this process cannot decrypt, so storing the audit key would silently re-mint it.</summary>
    private void SeedUnusableKeyring()
    {
        var keyringDir = Path.Combine(_dir, "audit-keyring");
        using (new EnvScope(SecureKeyring.PassphraseVariable, "the passphrase this daemon will not have"))
        using (new EnvScope(SecureKeyring.PassphraseFileVariable, null))
        {
            new SecureKeyring(keyringDir).SaveSecret("token_seed", "anything, just to materialize the ring");
        }
    }

    [Fact]
    public void AKeyringPostureFailure_RefusesTheBoot_InsteadOfFallingBackToTheInMemoryJournal()
    {
        SeedUnusableKeyring();

        using var noPass = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var noPassFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);

        var services = new ServiceCollection();
        var errors = new System.Collections.Generic.List<string>();

        var ex = Assert.Throws<AuditPersistenceUnavailableException>(() =>
            GatewayServiceRegistration.RegisterAuditLog(
                services,
                () => new AppDbContext(_dbPath),
                _dbPath,
                log: null,
                logError: (message, _) => errors.Add(message)));

        // The refusal explains itself AND names the remedy, because the operator reading it is
        // staring at a daemon that did not start.
        Assert.Contains("refusing to fall back to the in-memory journal", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SecureKeyring.PassphraseVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(errors, e => e.Contains("audit chain refused", StringComparison.Ordinal));

        // And nothing was registered: a half-wired fallback is what we are refusing.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAuditLog));
    }

    [Fact]
    public void AProtectedKeyring_RegistersTheChainedLog_AndLogsThePosture()
    {
        using var pass = new EnvScope(SecureKeyring.PassphraseVariable, "a perfectly good passphrase");
        using var noPassFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);

        var services = new ServiceCollection();
        var milestones = new System.Collections.Generic.List<string>();

        GatewayServiceRegistration.RegisterAuditLog(
            services, () => new AppDbContext(_dbPath), _dbPath, log: milestones.Add, logError: null);

        Assert.Contains(services, d => d.ServiceType == typeof(IChainedAuditLog));
        Assert.Contains(milestones, m => m.Contains("audit chain ready", StringComparison.Ordinal));
        // The posture is stated at boot whatever it is — nothing surfaced it before.
        Assert.Contains(milestones, m => m.Contains("keyring protection=Passphrase", StringComparison.Ordinal));
    }

    /// <summary>The fallback that IS legitimate: no daemon DB at all. It still reaches the operator as
    /// an error rather than as an informational milestone.</summary>
    [Fact]
    public void NoDaemonDb_StillFallsBack_ButSaysSoAtErrorLevel()
    {
        var services = new ServiceCollection();
        var errors = new System.Collections.Generic.List<string>();

        GatewayServiceRegistration.RegisterAuditLog(
            services, dbFactory: null, _dbPath, log: null, logError: (m, _) => errors.Add(m));

        Assert.Contains(services, d => d.ServiceType == typeof(IAuditLog));
        Assert.Contains(errors, e => e.Contains("EVENTS WILL NOT SURVIVE RESTART", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(UnprotectedKeyringException))]
    [InlineData(typeof(KeyringUnreadableException))]
    [InlineData(typeof(KeyringProtectorUnavailableException))]
    public void EveryKeyringPostureFailure_IsFatal(Type exceptionType)
    {
        Exception instance = exceptionType == typeof(UnprotectedKeyringException)
            ? new UnprotectedKeyringException(AuditCrypto.KeyName)
            : exceptionType == typeof(KeyringUnreadableException)
                ? new KeyringUnreadableException(AuditCrypto.KeyName)
                : new KeyringProtectorUnavailableException(AuditCrypto.KeyName, "a reason");

        Assert.True(GatewayServiceRegistration.IsKeyringPosture(instance));
    }

    [Fact]
    public void AStoreFailure_IsNotAKeyringPostureFailure_AndKeepsTheFallback()
        => Assert.False(GatewayServiceRegistration.IsKeyringPosture(new IOException("the db file is gone")));

    // ---- B3: the verify CLI is a reader ----

    [Fact]
    public void VerifyCli_DoesNotMintAMasterKey_OnABoxThatNeverRanTheDaemon()
    {
        using var noPass = new EnvScope(SecureKeyring.PassphraseVariable, null);
        using var noPassFile = new EnvScope(SecureKeyring.PassphraseFileVariable, null);
        using var noAllow = new EnvScope(SecureKeyring.AllowUnprotectedVariable, null);

        var exit = AuditCommands.Run(new[] { "audit", "verify", "--data", _dbPath });

        // It used to exit 1 here with "Refusing to store 'audit-payload-key'…" on any box without a
        // key-ring protector — a verification tool failing because it tried to WRITE.
        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(Path.Combine(_dir, "audit-keyring")),
            "verify must not create a key ring, let alone a key in it");
    }

    [Fact]
    public void VerifyCli_CannotVerifyWithoutTheMasterKey_AndSaysSoRatherThanReportingOk()
    {
        // A real chain, written under a key ring that is then taken away (a restored DB without its
        // keyring, the classic "we have the evidence but not the key" case).
        var keyringDir = Path.Combine(_dir, "audit-keyring");
        using (new EnvScope(SecureKeyring.PassphraseVariable, "a passphrase"))
        {
            var log = new ChainedAuditLog(
                () => new AppDbContext(_dbPath),
                new AuditCrypto(new SecureKeyring(keyringDir)),
                new AuditFileMirror(_dbPath + ".audit-mirror"));
            log.Append("test_probe", new System.Collections.Generic.Dictionary<string, string> { ["k"] = "v" }, "tester");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(keyringDir, recursive: true);

        Assert.Equal(1, AuditCommands.Run(new[] { "audit", "verify", "--data", _dbPath }));
    }

    [Fact]
    public void VerifyCli_StillVerifiesARealChain_WithTheKeyringPresent()
    {
        using var pass = new EnvScope(SecureKeyring.PassphraseVariable, "a passphrase");
        var keyringDir = Path.Combine(_dir, "audit-keyring");
        var log = new ChainedAuditLog(
            () => new AppDbContext(_dbPath),
            new AuditCrypto(new SecureKeyring(keyringDir)),
            new AuditFileMirror(_dbPath + ".audit-mirror"));
        log.Append("test_probe", new System.Collections.Generic.Dictionary<string, string> { ["k"] = "v" }, "tester");

        Assert.Equal(0, AuditCommands.Run(new[] { "audit", "verify", "--data", _dbPath }));
    }
}
