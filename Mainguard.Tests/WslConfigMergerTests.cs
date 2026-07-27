using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-05 #1-#3 / plan §6 #1-#2: the pure INI merger is the correctness heart of P2-05. Every case
// is fixture-tested against a committed expected file so a byte-level regression (lost user key,
// clobbered comment, mangled CRLF) fails the build. The tail of the file pins the other half of the
// same guarantee — how the merged result LANDS on disk (MG-32: atomic temp+swap, backup first), since
// .wslconfig is the machine's GLOBAL WSL2 config and a torn write there breaks every distro.
public class WslConfigMergerTests
{
    // The keys Mainguard wants under [wsl2]. Fixed here so the expected fixtures are stable.
    private static IReadOnlyDictionary<string, string> OurKeys() => new Dictionary<string, string>
    {
        ["memory"] = "6GB",
        ["autoMemoryReclaim"] = "gradual",
    };

    public static IEnumerable<object[]> FixtureCases() => new[]
    {
        new object[] { "empty" },          // brand-new file
        new object[] { "no-wsl2" },        // file with other sections, no [wsl2]
        new object[] { "existing-wsl2" },  // [wsl2] present with an unrelated key
        new object[] { "keys-set" },       // our keys already set — user value must win (no change)
        new object[] { "comments" },       // comments (# and ;) + a trailing unknown section
        new object[] { "crlf" },           // CRLF newlines preserved
    };

    // §6 #1 — Merge output matches the committed expected file, byte-for-byte.
    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void WslConfigMerger_Fixtures(string caseName)
    {
        var input = ReadFixture($"{caseName}.input.wslconfig");
        var expected = ReadFixture($"{caseName}.expected.wslconfig");

        var merged = WslConfigMerger.Merge(input, OurKeys());

        Assert.Equal(expected, merged);
    }

    // §6 #1 — a null (missing file) behaves like an empty file.
    [Fact]
    public void Merge_NullContent_CreatesSectionLikeEmptyFile()
    {
        var fromNull = WslConfigMerger.Merge(null, OurKeys());
        var fromEmpty = WslConfigMerger.Merge(string.Empty, OurKeys());
        Assert.Equal(fromEmpty, fromNull);
        Assert.Contains("[wsl2]", fromNull, StringComparison.Ordinal);
    }

    // Edge row 1 — an existing user value wins; ours is never written over it.
    [Fact]
    public void Merge_ShouldPreserveExistingUserKeys_AndAddOnlyOurs()
    {
        var input = ReadFixture("keys-set.input.wslconfig");
        var merged = WslConfigMerger.Merge(input, OurKeys());

        Assert.Equal(input, merged);                              // no change at all
        Assert.Contains("memory = 12GB", merged, StringComparison.Ordinal);   // user value kept
        Assert.DoesNotContain("6GB", merged, StringComparison.Ordinal);       // ours NOT applied
        Assert.Contains("dropcache", merged, StringComparison.Ordinal);
    }

    // §6 #2 — merging twice equals merging once (idempotent) for every fixture.
    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void Merge_ShouldBeIdempotent(string caseName)
    {
        var input = ReadFixture($"{caseName}.input.wslconfig");

        var once = WslConfigMerger.Merge(input, OurKeys());
        var twice = WslConfigMerger.Merge(once, OurKeys());

        Assert.Equal(once, twice);
    }

    // §6 #2 — the merger is pure: no instance state / IO surface, and deterministic across calls.
    [Fact]
    public void Merger_IsPure_NoIO()
    {
        var type = typeof(WslConfigMerger);
        Assert.True(type.IsAbstract && type.IsSealed, "WslConfigMerger must be a static class.");
        Assert.Empty(type.GetFields(System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic));

        var input = ReadFixture("comments.input.wslconfig");
        var a = WslConfigMerger.Merge(input, OurKeys());
        var b = WslConfigMerger.Merge(input, OurKeys());
        Assert.Equal(a, b);
    }

    // TI-P2-05 #3 — memory default = min(50% RAM, 8GB), floored to whole GB (min 1GB).
    [Theory]
    [InlineData(1L, "1GB")]                               // <1GB RAM floors to the 1GB minimum
    [InlineData(2L, "1GB")]                               // 2GB RAM → half = 1GB
    [InlineData(4L, "2GB")]
    [InlineData(8L, "4GB")]
    [InlineData(16L, "8GB")]                              // half = 8GB = cap
    [InlineData(32L, "8GB")]                              // half = 16GB → capped at 8GB
    [InlineData(64L, "8GB")]
    public void Merge_MemoryDefault_ShouldBeMinHalfRamOr8Gb(long ramGb, string expected)
    {
        var bytes = ramGb * 1024L * 1024 * 1024;
        Assert.Equal(expected, WslConfigMergeStep.ComputeMemoryValue(bytes));
    }

    // ---- Audit fix #12: uninstall reverts Mainguard's [wsl2] keys (conservatively) -------------------

    [Fact]
    public void Remove_MergeThenRemove_RestoresTheOriginalFileByteForByte()
    {
        var original = "# user notes\n[experimental]\nsparseVhd=true\n";
        var merged = WslConfigMerger.Merge(original, OurKeys());

        Assert.Equal(original, WslConfigMerger.RemoveMainguardKeys(merged));
    }

    [Fact]
    public void Remove_FreshFileCreatedByMerge_BecomesEmpty()
    {
        var merged = WslConfigMerger.Merge(null, OurKeys());

        Assert.Equal(string.Empty, WslConfigMerger.RemoveMainguardKeys(merged));
    }

    [Fact]
    public void Remove_UserTunedValues_Survive()
    {
        // The user edited memory after install (not our <N>GB shape) and set their own reclaim mode:
        // neither is ours to delete.
        var content = "[wsl2]\nmemory=12000MB\nautoMemoryReclaim=dropcache\nprocessors=4\n";

        Assert.Equal(content, WslConfigMerger.RemoveMainguardKeys(content));
    }

    [Fact]
    public void Remove_OurKeysAmongUserKeys_RemovesOnlyOurs_AndKeepsTheSection()
    {
        var content = "[wsl2]\nprocessors=4\nmemory=8GB\nautoMemoryReclaim=gradual\n\n[experimental]\nsparseVhd=true\n";

        var reverted = WslConfigMerger.RemoveMainguardKeys(content);

        Assert.Equal("[wsl2]\nprocessors=4\n\n[experimental]\nsparseVhd=true\n", reverted);
    }

    [Fact]
    public void Remove_IsIdempotent_AndNoOpWithoutWsl2Section()
    {
        var noSection = "[experimental]\nsparseVhd=true\n";
        Assert.Equal(noSection, WslConfigMerger.RemoveMainguardKeys(noSection));

        var merged = WslConfigMerger.Merge("[network]\ngenerateHosts=false\n", OurKeys());
        var once = WslConfigMerger.RemoveMainguardKeys(merged);
        Assert.Equal(once, WslConfigMerger.RemoveMainguardKeys(once));
    }

    [Fact]
    public void Remove_PreservesCrlfNewlines()
    {
        var content = "[wsl2]\r\nprocessors=2\r\nmemory=6GB\r\n";

        Assert.Equal("[wsl2]\r\nprocessors=2\r\n", WslConfigMerger.RemoveMainguardKeys(content));
    }

    // ---- MG-32: the ON-DISK write path (BootstrapFileSystem) ----------------------------------------
    // The merge above is pure; landing its result on disk is where the machine-wide risk lives.
    // %UserProfile%\.wslconfig configures EVERY WSL2 distro, so the write must never be observable in a
    // torn state: File.WriteAllText truncates in place and then streams, which under a crash — or under
    // a reader/another writer — leaves the user's GLOBAL config empty or half-written. These run against
    // the real BootstrapFileSystem in a temp directory.

    [Fact]
    public async System.Threading.Tasks.Task WriteWslConfig_ShouldNeverBeObservableTruncated_WhileBeingRewritten()
    {
        using var dir = new TempDir();
        var fs = new BootstrapFileSystem(dir.Path);

        // Big enough that an in-place truncate+stream write leaves a wide window a reader lands in.
        var a = "[wsl2]\n" + new string('a', 1024 * 1024) + "\n";
        var b = "[wsl2]\n" + new string('b', 1024 * 1024) + "\n";
        fs.WriteWslConfig(a);

        var torn = new System.Collections.Concurrent.ConcurrentBag<int>();
        var reads = 0;
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var stop = false;

        var reader = System.Threading.Tasks.Task.Run(() =>
        {
            while (!System.Threading.Volatile.Read(ref stop))
            {
                string observed;
                try
                {
                    // FileShare.ReadWrite: we are only observing, never blocking the writer.
                    using var stream = new FileStream(fs.WslConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(stream);
                    observed = sr.ReadToEnd();
                }
                catch (IOException) { continue; }              // transient share/rename race — not an observation
                catch (UnauthorizedAccessException) { continue; }

                System.Threading.Interlocked.Increment(ref reads);
                if (observed == a || observed == b)
                    seen.TryAdd(observed, 0);
                else
                    torn.Add(observed.Length);                  // ANY other content is a torn read
            }
        });

        for (var i = 0; i < 40 && seen.Count < 2; i++)
            fs.WriteWslConfig(i % 2 == 0 ? b : a);

        System.Threading.Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reads > 0, "the reader never observed the file — the test proved nothing");
        Assert.True(torn.IsEmpty,
            $"observed {torn.Count} torn .wslconfig read(s) (lengths: {string.Join(", ", torn)}) — the write is not atomic");
    }

    // Round-trips through the real filesystem: creating a missing file, replacing an existing one
    // byte-for-byte (incl. CRLF and no BOM — WSL parses this INI), and leaving no temp litter behind in
    // %UserProfile% from the temp+swap.
    [Fact]
    public void WriteWslConfig_ShouldReplaceContentExactly_AndLeaveNoTempFiles()
    {
        using var dir = new TempDir();
        var fs = new BootstrapFileSystem(dir.Path);

        Assert.Null(fs.ReadWslConfig());                          // nothing there yet
        fs.WriteWslConfig("[wsl2]\r\nmemory=6GB\r\n");            // create
        Assert.Equal("[wsl2]\r\nmemory=6GB\r\n", fs.ReadWslConfig());

        fs.WriteWslConfig("[wsl2]\nmemory=2GB\n");                // replace with SHORTER content
        Assert.Equal("[wsl2]\nmemory=2GB\n", fs.ReadWslConfig());

        // No BOM: File.WriteAllText emitted none and WSL's parser should not have to tolerate one.
        var bytes = File.ReadAllBytes(fs.WslConfigPath);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        Assert.Equal(new[] { ".wslconfig" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());
    }

    // Backup-before-write still holds (invariant §5.4) after the write became a temp+swap: the backup is
    // a copy of the PRE-write content, taken next to the file, and never clobbers an earlier one.
    [Fact]
    public void BackupWslConfig_ShouldSnapshotPreWriteContent_AndNeverClobberAnEarlierBackup()
    {
        using var dir = new TempDir();
        var fs = new BootstrapFileSystem(dir.Path);

        fs.BackupWslConfig();                                     // missing file → no-op, no throw
        Assert.Empty(Directory.GetFiles(dir.Path));

        fs.WriteWslConfig("[wsl2]\nmemory=6GB\n");
        fs.BackupWslConfig();
        fs.WriteWslConfig("[wsl2]\nmemory=2GB\n");
        fs.BackupWslConfig();

        var backups = Directory.GetFiles(dir.Path, ".wslconfig.mainguard.*.bak");
        Assert.Equal(2, backups.Length);                          // second-resolution collision uniquified
        var contents = backups.Select(File.ReadAllText).ToArray();
        Assert.Contains("[wsl2]\nmemory=6GB\n", contents);
        Assert.Contains("[wsl2]\nmemory=2GB\n", contents);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("mainguard-wslconfig-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string ReadFixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        // ReadAllText preserves the file's exact newline bytes — essential for the CRLF fixture.
        return File.ReadAllText(Path.Combine(dir ?? AppContext.BaseDirectory,
            "Mainguard.Tests", "Fixtures", "WslConfig", name));
    }
}
