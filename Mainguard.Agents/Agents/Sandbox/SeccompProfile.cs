using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The seccomp profile applied to every agent container (P2-07, G-15 + G2 control 3). It is the
/// <b>canonical moby/containerd default-deny profile</b> (<c>defaultAction: SCMP_ACT_ERRNO</c>, the
/// standard <c>archMap</c>, and the ~300-syscall allowlist) with the three cross-process
/// memory-inspection syscalls — <c>ptrace</c>, <c>process_vm_readv</c>, <c>process_vm_writev</c> —
/// removed from every allow rule and denied by an explicit <c>SCMP_ACT_ERRNO</c> rule. So the agent
/// gets the full default hardening (<c>mount</c>/<c>bpf</c>/<c>pivot_root</c> stay cap-gated and,
/// under <c>CapDrop ALL</c>, unreachable; <c>kexec_load</c> et al. are default-denied) AND cannot
/// scrape the OOB key <c>K</c> from the supervisor process's memory (OPS §6.1 decision C).
///
/// <para><b>Single source of truth.</b> The profile is the checked-in
/// <c>images/mainguard-agent-base/seccomp.json</c>, embedded into this assembly. <see cref="Json"/>
/// returns that exact content, so what the pure test asserts equals what <c>ContainerSpecBuilder</c>
/// passes in <c>seccomp=&lt;json&gt;</c> equals what the container runs. A custom <c>seccomp=</c> in
/// <c>SecurityOpt</c> <b>replaces</b> Docker's default (it is not additive), which is exactly why this
/// profile reproduces that default rather than overlaying it. It is never <c>unconfined</c>.</para>
/// </summary>
public static class SeccompProfile
{
    private const string ResourceName = "Mainguard.Agents.Agents.Sandbox.seccomp.json";

    /// <summary>The syscalls this profile structurally denies (G2 control 3).</summary>
    public static readonly IReadOnlyList<string> DeniedSyscalls = new[]
    {
        "ptrace",
        "process_vm_readv",
        "process_vm_writev",
    };

    /// <summary>The seccomp action the profile applies to the denied syscalls.</summary>
    public const string DenyAction = "SCMP_ACT_ERRNO";

    /// <summary>
    /// The default-deny profile JSON, loaded once from the embedded
    /// <c>images/mainguard-agent-base/seccomp.json</c>. This is the authoritative content passed to
    /// Docker and asserted by the tests.
    /// </summary>
    public static string Json { get; } = LoadEmbeddedProfile();

    /// <summary>The full <c>SecurityOpt</c> value Docker consumes (<c>seccomp=&lt;json&gt;</c>).</summary>
    public static string SecurityOptValue => "seccomp=" + Json;

    /// <summary>
    /// Does <paramref name="profileJson"/> <b>really</b> deny <see cref="DeniedSyscalls"/>? Returns null
    /// when it does, or a sentence naming the first gap when it does not.
    ///
    /// <para><b>Why this exists rather than a substring search.</b> The spec-time guard in
    /// <c>ContainerSpecBuilder.AssertG2Controls</c> used to ask whether the string <c>"ptrace"</c>
    /// appeared anywhere in the <c>seccomp=&lt;json&gt;</c> blob. The stock moby profile — the one with
    /// no hardening at all — contains all three names in its <b>allow</b> group, so the guard for this
    /// profile's <i>sole</i> hardening delta was one the un-hardened upstream profile also passes.
    /// Measured: dropping the deny rules and restoring the upstream allow entries left the guard green.
    /// The check has to read the rules' ACTIONS, which is what this does — the same two-sided assertion
    /// (<c>in a deny group</c> AND <c>in no allow group</c>) that <c>SeccompProfileTests</c> makes.</para>
    ///
    /// <para>A name in an allow group is fatal even when it is also in a deny group: libseccomp applies
    /// the first matching rule, so an allow entry can shadow the denial outright.</para>
    /// </summary>
    public static string? DescribeDenialGap(string profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
            return "the seccomp profile document is empty.";

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(profileJson).RootElement;
        }
        catch (JsonException ex)
        {
            return $"the seccomp profile is not valid JSON, so nothing about it can be checked: {ex.Message}";
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("defaultAction", out var defaultAction)
            || defaultAction.GetString() != DenyAction)
        {
            return $"the seccomp profile's defaultAction is not '{DenyAction}' — it is not a default-deny profile, "
                   + "so every syscall it does not name is permitted.";
        }

        if (!root.TryGetProperty("syscalls", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return "the seccomp profile carries no 'syscalls' rules at all.";

        var denied = NamesWithAction(groups, DenyAction);
        var allowed = NamesWithAction(groups, "SCMP_ACT_ALLOW");

        foreach (var syscall in DeniedSyscalls)
        {
            if (allowed.Contains(syscall))
            {
                return $"G2 control 3: the seccomp profile ALLOWS '{syscall}'. This is the un-hardened upstream "
                       + "profile's posture — the memory-inspection denials are this profile's only delta over "
                       + "stock moby, and an allow rule shadows any deny rule that follows it.";
            }

            if (!denied.Contains(syscall))
            {
                return $"G2 control 3: the seccomp profile has no '{DenyAction}' rule for '{syscall}', so it is "
                       + "reachable by whatever the default action permits.";
            }
        }

        return null;
    }

    private static HashSet<string> NamesWithAction(JsonElement groups, string action)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("action", out var groupAction)
                || groupAction.GetString() != action
                || !group.TryGetProperty("names", out var groupNames)
                || groupNames.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var name in groupNames.EnumerateArray().Select(n => n.GetString()))
            {
                if (name is not null)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static string LoadEmbeddedProfile()
    {
        var assembly = typeof(SeccompProfile).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded seccomp profile '{ResourceName}' is missing; it must be embedded from images/mainguard-agent-base/seccomp.json.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
