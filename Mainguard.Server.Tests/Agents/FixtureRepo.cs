using System.IO;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The tiny <b>Node</b> project the Docker merge-queue suites verify.
///
/// <para>Node rather than Mainguard on purpose: the jail's base image carries <c>nodejs_22</c>, so
/// <c>.mainguard/verify</c> is a real toolchain running a real test file whose <b>exit code</b> is the only
/// thing the merge queue reads — and which can therefore genuinely fail. Mainguard's own verify command
/// (<c>dotnet test</c>) cannot complete inside a jail today, and a fixture that cannot fail would make
/// every assertion built on it vacuous.</para>
///
/// <para>Shared (rather than copied into each suite) because the verification command, the marker string
/// and the seeded tree are what the assertions compare against: two copies would drift, and a suite reading
/// a stale <c>VerifyCommand</c> would assert provenance against a command the repo no longer declares.</para>
/// </summary>
internal static class FixtureRepo
{
    public const string VerifyCommand = "node .mainguard/verify.js";
    public const string PassMarker = "fixture verification passed";

    /// <summary>An UNTRACKED, worktree-only dwell knob. Untracked on purpose: anything in the tree
    /// would differ between a branch and main and arm the RT-D2 gate, which is a different test.</summary>
    public const string DelayFile = ".verify-delay-ms";

    public const string CalcJs =
        "exports.add = (a, b) => a + b;\n" +
        "exports.mul = (a, b) => a * b;\n";

    private const string VerifyJs = """
        // The fixture project's test suite. Its EXIT CODE is the only thing the merge queue reads
        // (OPS SA-1: the daemon-observed container-runtime exit), so it must be able to fail.
        const assert = require('node:assert');
        const fs = require('node:fs');
        const path = require('node:path');

        // An untracked, worktree-only dwell so a test can keep the re-verification window observable.
        const delayFile = path.join(__dirname, '..', '.verify-delay-ms');
        if (fs.existsSync(delayFile)) {
          const ms = parseInt(fs.readFileSync(delayFile, 'utf8').trim(), 10) || 0;
          if (ms > 0) {
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
          }
        }

        const calc = require('../src/calc.js');
        assert.strictEqual(calc.add(2, 2), 4);
        assert.strictEqual(calc.mul(3, 4), 12);
        console.log('fixture verification passed');

        """;

    public static void Seed(string repoPath)
    {
        WriteFile(repoPath, ".mainguard/verify", VerifyCommand + "\n");
        WriteFile(repoPath, ".mainguard/verify.js", VerifyJs);
        WriteFile(repoPath, "src/calc.js", CalcJs);
        WriteFile(repoPath, "README.md", "A tiny Node project the merge queue can actually verify.\n");
    }

    private static void WriteFile(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
