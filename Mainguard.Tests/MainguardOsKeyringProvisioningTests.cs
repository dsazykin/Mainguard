using System;
using System.IO;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// F53, the shipped half — how the key-ring passphrase ARRIVES on a MainguardOS VM.
///
/// <para><see cref="SecureKeyringProtectionTests"/> and <see cref="SecureKeyringMigrationTests"/>
/// cover the protector once a passphrase is in hand, including the
/// <c>MAINGUARD_KEYRING_PASSPHRASE_FILE</c> form. What neither covers is that anything ever PUTS a
/// passphrase there. Nothing sets it on a VM: the unit mints one in <c>ExecStartPre</c> and names
/// the file in <c>Environment=</c>, a four-link chain (Dockerfile COPY → ExecStartPre → Environment
/// → <c>PassphraseKeyDerivation.Resolve</c>) that shipped with no test on any link.</para>
///
/// <para>Since the daemon is now fail-closed on Linux, a break in that chain is not a degraded
/// install — it is a VM whose daemon refuses to start. That is exactly how it showed up: the P2-21
/// payload job starts the shipped daemon itself, did not replay ExecStartPre, and the daemon
/// (correctly) exited 78. These are the cheap, Docker-free guards for the same chain.</para>
/// </summary>
public sealed class MainguardOsKeyringProvisioningTests
{
    private const string ScriptPath = "/opt/mainguard/provision-keyring-passphrase.sh";
    private const string PassphrasePath = "/etc/mainguard/keyring.passphrase";

    [Fact]
    public void TheShippedUnit_MintsThePassphrase_AndNamesItWithTheVariableTheDaemonReads()
    {
        var unit = ReadPayloadFile("mainguardd.service");
        var dockerfile = ReadPayloadFile("Dockerfile");

        // Link 1 — the script is in the image, executable, at the path the unit invokes.
        Assert.Contains($"COPY provision-keyring-passphrase.sh {ScriptPath}", dockerfile, StringComparison.Ordinal);
        Assert.Contains($"chmod 0755 {ScriptPath}", dockerfile, StringComparison.Ordinal);

        // Link 2 — before the daemon, and as ROOT. The `+` prefix is what lets a User=mainguard
        // service write into root-owned /etc/mainguard; without it the script mints nothing.
        Assert.Contains($"ExecStartPre=+{ScriptPath}", unit, StringComparison.Ordinal);

        // Link 3 — the variable the daemon actually reads, carrying the PATH and not the secret.
        // Handing the passphrase itself through Environment= would publish it to `systemctl show`,
        // /proc/<pid>/environ and every agent container the daemon spawns.
        Assert.Contains(
            $"Environment={SecureKeyring.PassphraseFileVariable}={PassphrasePath}", unit, StringComparison.Ordinal);
        Assert.DoesNotContain($"Environment={SecureKeyring.PassphraseVariable}=", unit, StringComparison.Ordinal);

        // Neither the unit nor the image may paper over a failed provisioning by accepting the
        // plaintext posture — that boots fine and ships the audit master key in cleartext, which is
        // the one outcome F53 exists to prevent.
        Assert.DoesNotContain(SecureKeyring.AllowUnprotectedVariable, unit, StringComparison.Ordinal);
        Assert.DoesNotContain(SecureKeyring.AllowUnprotectedVariable, dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvisioningScript_WritesTheFileTheUnitNames_AndNeverRegeneratesIt()
    {
        var script = ReadPayloadFile("provision-keyring-passphrase.sh");

        // Link 4 — the script writes the path the unit points the daemon at.
        Assert.Contains("DIR=/etc/mainguard", script, StringComparison.Ordinal);
        Assert.Contains("FILE=\"$DIR/keyring.passphrase\"", script, StringComparison.Ordinal);

        // Once only. A second passphrase would re-key the ring, making the audit master key — and
        // every payload encrypted under it — permanently unreadable, with no way back.
        Assert.Contains("if [ -s \"$FILE\" ]; then", script, StringComparison.Ordinal);

        // Per INSTALL, from the kernel CSPRNG — never a constant. The MainguardOS tarball is
        // byte-reproducible, so a baked-in secret would be the same one on every machine on earth.
        Assert.Contains("/dev/urandom", script, StringComparison.Ordinal);
        Assert.DoesNotContain(PassphrasePath, ReadPayloadFile("Dockerfile"), StringComparison.Ordinal);
    }

    /// <summary>The script is COPY'd into the image, so it is a build input: a COPY the inputs hash
    /// does not cover is a payload that can change without its <c>/etc/mainguardos-release</c> stamp
    /// changing. build.sh has its own coverage self-check; this is the one that runs without Docker.</summary>
    [Fact]
    public void TheProvisioningScript_IsCoveredByTheBuildInputsHash()
    {
        var buildSh = ReadPayloadFile("build.sh");
        var start = buildSh.IndexOf("INPUT_SPECS=(", StringComparison.Ordinal);
        Assert.True(start >= 0, "build.sh no longer declares INPUT_SPECS");
        var specs = buildSh[start..];
        specs = specs[..specs.IndexOf("\n)", StringComparison.Ordinal)];

        Assert.Contains("\"provision-keyring-passphrase.sh\"", specs, StringComparison.Ordinal);
    }

    private static string ReadPayloadFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mainguard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "build", "mainguardos", name));
    }
}
