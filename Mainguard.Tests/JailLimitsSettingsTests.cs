using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>The operator's per-jail ceiling (2026-09-04): defaults until set, clamped on the way in and
/// again on the way out of the file, persisted, and audited.</summary>
public sealed class JailLimitsSettingsTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void NothingPersisted_IsTheCompiledDefault()
    {
        var settings = new JailLimitsSettings(new InMemoryJailLimitsStore());
        Assert.Equal(SandboxLimits.Default, settings.Current);
        Assert.True(settings.IsDefault);
    }

    [Fact]
    public void Set_ClampsToTheBand_PersistsAndAnswersWithWhatItPersisted()
    {
        var store = new InMemoryJailLimitsStore();
        var audit = new InMemoryAuditLog();
        var settings = new JailLimitsSettings(store, audit);

        var persisted = settings.Set(memoryBytes: 1, cpus: 1000, actor: "op");

        Assert.Equal(JailLimitsSettings.MinMemoryBytes, persisted.MemoryBytes);
        Assert.Equal(JailLimitsSettings.MaxCpus, persisted.Cpus);
        Assert.Equal(SandboxLimits.Default.Pids, persisted.Pids); // only memory and CPUs are the operator's
        Assert.Equal(persisted, settings.Current);
        Assert.False(settings.IsDefault);
        Assert.Equal(new JailLimitsDocument(JailLimitsSettings.MinMemoryBytes, JailLimitsSettings.MaxCpus), store.Load());
        var changed = Assert.Single(audit.Read(), e => e.Type == JailLimitsSettings.ChangedEvent);
        Assert.Equal("op", changed.Fields["actor"]);
        Assert.Equal(SandboxLimits.Default.MemoryBytes.ToString(), changed.Fields["previous_memory_bytes"]);
    }

    [Fact]
    public void JsonStore_RoundTrips_AndAGarbageFileReadsAsNothingPersisted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mg-jail-limits-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = Path.Combine(dir, "mainguard-jail-limits.json");
            new JailLimitsSettings(new JsonJailLimitsStore(path)).Set(3 * GiB, 1.5, "op");

            var reread = new JailLimitsSettings(new JsonJailLimitsStore(path));
            Assert.Equal(3 * GiB, reread.Current.MemoryBytes);
            Assert.Equal(1.5, reread.Current.Cpus);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(SandboxLimits.Default, new JailLimitsSettings(new JsonJailLimitsStore(path)).Current);

            // A hand-edited zero cannot become a ceiling of zero: the read clamps too.
            File.WriteAllText(path, "{\"MemoryBytes\":0,\"Cpus\":0}");
            var clamped = new JailLimitsSettings(new JsonJailLimitsStore(path)).Current;
            Assert.Equal(JailLimitsSettings.MinMemoryBytes, clamped.MemoryBytes);
            Assert.Equal(JailLimitsSettings.MinCpus, clamped.Cpus);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
