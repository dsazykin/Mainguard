using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Mainguard.Git.Security;

namespace Mainguard.Tests;

/// <summary>
/// Gives this test process a key-ring posture ON PURPOSE, before any test runs.
///
/// <para>The audit master key is fail-closed: on a platform with no at-rest protector for the
/// DataProtection key ring — Linux/WSL, and a macOS box whose login Keychain will not serve the
/// master key — <see cref="SecureKeyring"/> refuses to store it, and the daemon now refuses to boot
/// rather than fall back to a journal that forgets everything at shutdown. A test suite that opens a
/// real <c>ChainedAuditLog</c> therefore has to say which posture it is testing, exactly as the
/// shipped Mainguard OS unit does (<c>build/mainguardos/mainguardd.service</c> hands the daemon a
/// passphrase file).</para>
///
/// <para>Setting the passphrase HERE rather than in <c>ci.yml</c> is deliberate: the suite then
/// exercises the same protected path on every machine it runs on — a developer's Linux box, a
/// Windows runner, a Mac — instead of passing on one OS by accident of platform and needing a CI
/// environment variable to pass on another. Nothing is loosened: the refusal itself is still
/// asserted, end to end, by <c>SecureKeyringProtectionTests</c>, which clears these variables for
/// the duration of those tests.</para>
/// </summary>
internal static class KeyringTestPosture
{
    [ModuleInitializer]
    internal static void Apply()
    {
        if (SecureKeyring.ResolveProtection() != KeyringProtection.None)
        {
            return; // DPAPI or a working login Keychain: already protected, leave it alone.
        }

        // Random per run, so nothing a test writes under it outlives the process in a shared temp
        // directory in a form some later run could decrypt.
        Environment.SetEnvironmentVariable(
            SecureKeyring.PassphraseVariable,
            "mainguard-tests-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    }
}
