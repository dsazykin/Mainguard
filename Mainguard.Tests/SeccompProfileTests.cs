using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Pins the shipped seccomp profile as a genuine <b>default-deny</b> profile (the canonical moby
/// default + the three memory-inspection denials), not an ALLOW-all overlay. Because a custom
/// <c>seccomp=</c> replaces Docker's default, this profile is the whole hardening story — so it must
/// deny by default and must not allowlist known-dangerous syscalls.
/// </summary>
public class SeccompProfileTests
{
    private static JsonElement Root() => JsonDocument.Parse(SeccompProfile.Json).RootElement;

    private static IEnumerable<JsonElement> Groups() => Root().GetProperty("syscalls").EnumerateArray();

    private static HashSet<string> NamesWithAction(string action) =>
        Groups()
            .Where(g => g.GetProperty("action").GetString() == action)
            .SelectMany(g => g.GetProperty("names").EnumerateArray().Select(n => n.GetString()!))
            .ToHashSet();

    [Fact]
    public void DefaultAction_IsDenyByDefault_NotAllow()
    {
        Assert.Equal("SCMP_ACT_ERRNO", Root().GetProperty("defaultAction").GetString());
    }

    [Fact]
    public void HasArchMap_AndSubstantialAllowlist()
    {
        Assert.True(Root().GetProperty("archMap").GetArrayLength() >= 1);
        // The real moby allowlist is hundreds of syscalls — a hand-rolled overlay would be tiny.
        Assert.True(NamesWithAction("SCMP_ACT_ALLOW").Count > 200);
    }

    [Fact]
    public void MemoryInspectionSyscalls_AreDenied_AndInNoAllowRule()
    {
        var allowed = NamesWithAction("SCMP_ACT_ALLOW");
        var denied = NamesWithAction("SCMP_ACT_ERRNO");

        foreach (var syscall in SeccompProfile.DeniedSyscalls)
        {
            Assert.Contains(syscall, denied);       // explicitly denied
            Assert.DoesNotContain(syscall, allowed); // and reachable via no allow rule
        }
    }

    [Fact]
    public void DangerousSyscalls_AreNotUnconditionallyAllowed()
    {
        // kexec_load must be in no allow rule at all; bpf/mount/pivot_root must only ever appear in a
        // capability-gated allow group (excluded under CapDrop ALL), never in an unconditional one.
        var unconditionalAllow = Groups()
            .Where(g => g.GetProperty("action").GetString() == "SCMP_ACT_ALLOW")
            .Where(g => !HasCapGate(g))
            .SelectMany(g => g.GetProperty("names").EnumerateArray().Select(n => n.GetString()!))
            .ToHashSet();

        Assert.DoesNotContain("kexec_load", unconditionalAllow);
        Assert.DoesNotContain("bpf", unconditionalAllow);
        Assert.DoesNotContain("mount", unconditionalAllow);
        Assert.DoesNotContain("pivot_root", unconditionalAllow);
    }

    [Fact]
    public void Profile_IsNeverUnconfined()
    {
        Assert.DoesNotContain("unconfined", SeccompProfile.Json);
        Assert.StartsWith("seccomp=", SeccompProfile.SecurityOptValue);
    }

    // ---- the spec-time guard, and the profile it used to accept ------------------------------------

    /// <summary>
    /// The shipped profile passes the guard <c>ContainerSpecBuilder</c> applies to every create request.
    /// The control for the two inversions below — without it they would also pass against a guard that
    /// rejected everything.
    /// </summary>
    [Fact]
    public void TheShippedProfile_PassesTheSpecTimeGuard()
    {
        Assert.Null(SeccompProfile.DescribeDenialGap(SeccompProfile.Json));
    }

    /// <summary>
    /// <b>The reason the guard was rewritten.</b> It used to be a substring search over the whole
    /// <c>seccomp=&lt;json&gt;</c> blob, so it only asked whether the NAME <c>ptrace</c> occurred
    /// somewhere. Stock moby's profile — the one carrying none of this profile's hardening — lists all
    /// three memory-inspection syscalls in its <b>allow</b> group, so it contains every one of those
    /// names and sailed through. The guard for the profile's sole delta over upstream was one upstream
    /// itself passed.
    ///
    /// <para>This is that exact document: default-deny, a substantial allowlist, all three names
    /// present — and every one of them ALLOWED. It must be refused.</para>
    /// </summary>
    [Fact]
    public void TheGuard_RejectsTheUnhardenedUpstreamProfile_WhichNamesTheSyscallsInItsAllowGroup()
    {
        var upstreamShaped = """
        {
          "defaultAction": "SCMP_ACT_ERRNO",
          "archMap": [ { "architecture": "SCMP_ARCH_X86_64", "subArchitectures": [ "SCMP_ARCH_X86" ] } ],
          "syscalls": [
            { "names": [ "read", "write", "ptrace", "process_vm_readv", "process_vm_writev" ],
              "action": "SCMP_ACT_ALLOW" }
          ]
        }
        """;

        var gap = SeccompProfile.DescribeDenialGap(upstreamShaped);

        Assert.NotNull(gap);
        Assert.Contains("ALLOWS", gap, StringComparison.Ordinal);

        // …and the OLD guard's predicate, evaluated on the same bytes, says this document is fine. That
        // is the finding, kept executable: without this line the test above proves the new guard rejects
        // *something*, but not that it rejects something the shipped guard used to accept.
        Assert.All(
            SeccompProfile.DeniedSyscalls,
            syscall => Assert.Contains(syscall, upstreamShaped, StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half: a profile that simply drops the deny rules (naming none of the three anywhere) is
    /// refused too. Split from the test above because a guard that only looked for an allow rule would
    /// pass this one, and "denied by the default action" is not the same fact as "explicitly denied" —
    /// the default action is exactly what an edit to <c>defaultAction</c> would change.
    /// </summary>
    [Fact]
    public void TheGuard_RejectsAProfileThatSimplyDroppedTheDenials()
    {
        var noDenials = """
        {
          "defaultAction": "SCMP_ACT_ERRNO",
          "archMap": [ { "architecture": "SCMP_ARCH_X86_64", "subArchitectures": [ "SCMP_ARCH_X86" ] } ],
          "syscalls": [ { "names": [ "read", "write" ], "action": "SCMP_ACT_ALLOW" } ]
        }
        """;

        var gap = SeccompProfile.DescribeDenialGap(noDenials);

        Assert.NotNull(gap);
        Assert.Contains("ptrace", gap, StringComparison.Ordinal);
    }

    /// <summary>An ALLOW-all overlay is refused on the default action alone, before any rule is read.</summary>
    [Fact]
    public void TheGuard_RejectsAnAllowByDefaultProfile()
    {
        var allowAll = """{ "defaultAction": "SCMP_ACT_ALLOW", "syscalls": [] }""";

        Assert.NotNull(SeccompProfile.DescribeDenialGap(allowAll));
    }

    private static bool HasCapGate(JsonElement group)
        => group.TryGetProperty("includes", out var inc)
           && inc.TryGetProperty("caps", out var caps)
           && caps.ValueKind == JsonValueKind.Array
           && caps.GetArrayLength() > 0;
}
