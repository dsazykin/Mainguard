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
    /// would differ between a branch and main and arm the RT-D2 gate, which is a different test.
    ///
    /// <para><b>Git-IGNORED is what makes "untracked" hold</b>, and the seed writes the
    /// <see cref="GitIgnore"/> that does it. Leaving the knob merely unadded is not enough once a stale
    /// cascade runs over the same worktree: <c>KeepAliveRebaser</c> preserves an agent's uncommitted work
    /// across the reparent by committing <c>git add -A</c> as <c>wip: sync</c> BEFORE it rebases, and
    /// <c>add -A</c> stages untracked files. The knob was therefore swept into the agent's tree — the one
    /// thing this constant's own doc says must never happen — and a test that then removed it from the
    /// worktree turned it into an unstaged deletion, which <c>git rebase</c> refuses outright ("cannot
    /// rebase: You have unstaged changes"). The cascade reported the branch un-reparented and the entry
    /// never came back to <c>Verified</c>. See the phase-3 decisions doc §22.</para></summary>
    public const string DelayFile = ".verify-delay-ms";

    /// <summary>The seeded ignore list. Committed on <c>main</c> before any branch exists, so it is
    /// byte-identical on every branch and arms no gate.</summary>
    public const string GitIgnore = ".gitignore";

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
        // First, because everything else about the dwell knob rests on it — see DelayFile.
        WriteFile(repoPath, GitIgnore, DelayFile + "\n");
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
