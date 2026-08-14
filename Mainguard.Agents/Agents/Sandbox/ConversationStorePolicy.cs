using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>One declared conversation path, resolved into the bind mount that carries it.</summary>
/// <param name="HostPath">The daemon-owned ext4 directory
/// (<c>&lt;vmRoot&gt;/conversations/&lt;repoHash&gt;/&lt;agentId&gt;/&lt;homeRelative&gt;</c>).</param>
/// <param name="HomeRelativePath">The adapter's declared <c>$HOME</c>-relative path, exactly as it
/// appears in the manifest — the CLI's own state location, which is what makes the mount line up.</param>
public sealed record ConversationMount(string HostPath, string HomeRelativePath)
{
    /// <summary>Where this mount appears inside the jail: <c>$HOME/&lt;homeRelative&gt;</c>.</summary>
    public string SandboxTarget => ConversationStorePolicy.SandboxTarget(HomeRelativePath);
}

/// <summary>
/// The pure, IO-free heart of the per-agent <b>conversation store</b>: where a CLI's conversation
/// transcripts live on the VM, where they appear in the jail, which declared paths may hold them, and
/// how a jail proves it really has them. Everything here is a constant, a string builder or a
/// comparison, so the whole policy is unit-assertable with no VM, no Docker daemon and no container.
///
/// <para><b>The defect this closes</b>, in the owner's words during live testing: <i>"i think i managed
/// to resume an agent's session, but i cant access the previous claude code conversation"</i>. The jail's
/// <c>$HOME</c> is a 256 MiB <b>tmpfs</b> (<see cref="ContainerSpecBuilder.AgentHome"/>) and Claude
/// Code keeps its transcripts under <c>$HOME/.claude/projects/&lt;escaped-cwd&gt;/&lt;session&gt;.jsonl</c>,
/// so they die with the container. Resuming a stranded queue entry (<c>AgentResumeService</c>) adopts the
/// branch and the agent id but spawns a <b>new</b> container, so the CLI comes up with no history: the
/// continuity resume exists to provide is exactly what was lost.</para>
///
/// <para><b>Why this is a BIND MOUNT and not a harvest-on-stop round trip.</b> The repository already has
/// a mechanism for carrying CLI state across a teardown — <c>credentialPaths</c>, harvested by
/// <c>SandboxAgentLauncher.HarvestCliCredentialsAsync</c> on stop and restored on spawn — and it is the
/// wrong shape for this problem, not merely a heavier one. The event that makes you NEED the conversation
/// back is the jail dying <b>without a clean stop</b>: a VM crash, a <c>docker rm</c>, a WSL restart. That
/// is the definition of a stranded queue entry, which is what resume is for. Harvest runs inside
/// <c>StopAsync</c>; in the crash case <c>StopAsync</c> never runs. A harvest-based design would therefore
/// pass every test anyone wrote for it and fail in every situation a user actually hits — this codebase's
/// signature defect, available in advance. A bind mount has nothing to run at teardown because it has
/// nothing to copy: the CLI writes straight into daemon-owned ext4 as it goes, and a crash loses
/// nothing.</para>
///
/// <para><b>Why the mount lines up across jails.</b> The transcript directory is keyed by the CLI's
/// working directory, and the jail's <c>WorkingDir</c> is the fixed
/// <see cref="ContainerSpecBuilder.WorkspaceTarget"/> (<c>/workspace</c>) for every jail of every agent.
/// So the escaped-cwd directory a re-spawned CLI looks in is the same one the dead jail wrote — which is
/// what makes a remount, rather than a copy, sufficient. (Verified on the shipped layout: an escaped-cwd
/// directory is the absolute path with its separators and dots replaced by <c>-</c>.)</para>
///
/// <para><b>Why the store is PER (repo, agent) and never shared.</b> The same reasoning
/// <see cref="PackageCachePolicy"/> makes, plus one more: a transcript is the most sensitive thing an
/// agent produces — it contains the repository's code, the operator's prompts, and whatever the CLI read
/// along the way. Two agents sharing one is a cross-tenant read of all of it. And the key is the PAIR:
/// agent ids are unique per repo and not globally (the external-PR intake names entries <c>pr-&lt;n&gt;</c>,
/// so two subscribed repositories both hold a <c>pr-7</c>), a collision this codebase has fixed four
/// separate times.</para>
///
/// <para><b>What is deliberately NOT declarable here.</b> A credential. See
/// <see cref="AssertNoCredentialOverlap"/>: a conversation path that contains or is contained by a
/// declared credential path is a typed refusal, not a filter and not a comment. That single rule is what
/// keeps "logins live only in the host OS keychain" true while a tree that deliberately outlives the jail
/// exists at all.</para>
/// </summary>
public static class ConversationStorePolicy
{
    /// <summary>The <c>&lt;vmRoot&gt;</c> child holding every per-agent conversation store. A sibling of
    /// <c>repos/</c>, <c>worktrees/</c>, <c>agents/</c> and <c>caches/</c>, and provisioned alongside them
    /// by <see cref="UsernsRemapPolicy.MountOwnershipScript"/>.</summary>
    public const string ConversationsDirectoryName = "conversations";

    /// <summary>
    /// Where one declared path appears inside the jail: under the agent's <c>$HOME</c>, at exactly the
    /// path the CLI reads.
    ///
    /// <para>This target is inside the tmpfs <c>$HOME</c> <b>on purpose</b>, and it is the one thing about
    /// this feature that differs from <see cref="PackageCachePolicy.SandboxMount"/>. A package cache can
    /// live anywhere because a package manager is TOLD where it is through the environment; a CLI's
    /// conversation directory is not configurable, so the store has to appear where the vendor put it.
    /// The bind mount is applied over the tmpfs at container create (the engine orders mounts
    /// parent-first), so the deeper path is a real ext4 directory while everything else under
    /// <c>$HOME</c> stays throwaway. What must be outside the tmpfs is the mount's <b>source</b>, and
    /// <see cref="ContainerSpecBuilder"/> asserts exactly that.</para>
    /// </summary>
    public static string SandboxTarget(string homeRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRelativePath);
        return ContainerSpecBuilder.AgentHome + "/" + homeRelativePath.Trim('/');
    }

    // ---- Layout (pure) ---------------------------------------------------------------------------

    /// <summary><c>&lt;vmRoot&gt;/conversations</c> — the root every store hangs off. Never mounted.</summary>
    public static string StoreRoot(string vmRoot)
        => Path.Combine(RequireVmRoot(vmRoot), ConversationsDirectoryName);

    /// <summary><c>&lt;vmRoot&gt;/conversations/&lt;repoHash&gt;</c> — one repository's stores. Never
    /// mounted: a jail that mounted this could read every other agent's transcripts for the repo.</summary>
    public static string RepoStoreDirectory(string vmRoot, string repoHash)
        => Path.Combine(StoreRoot(vmRoot), RequireHandle(repoHash));

    /// <summary><c>&lt;vmRoot&gt;/conversations/&lt;repoHash&gt;/&lt;agentId&gt;</c> — this agent's whole
    /// store. Also never mounted as a unit: the mounts are the per-declared-path leaves below it, so a
    /// future adapter that declares two paths cannot accidentally hand the jail the parent.</summary>
    public static string AgentStorePath(string vmRoot, string repoHash, string agentId)
        => Path.Combine(RepoStoreDirectory(vmRoot, repoHash), AgentRepoLayout.RequireAgentId(agentId));

    /// <summary>
    /// The daemon-side directory backing ONE declared path — the store mirrors the CLI's own
    /// <c>$HOME</c> layout underneath the agent's store root, so <c>.claude/projects</c> is at
    /// <c>&lt;agentStore&gt;/.claude/projects</c>. Mirroring rather than slugifying keeps the tree
    /// readable to a human debugging it, and means a second declared path can never collide with the
    /// first's contents.
    /// </summary>
    public static string DeclaredPathStore(string vmRoot, string repoHash, string agentId, string homeRelativePath)
    {
        if (!Adapters.AdapterManifest.IsHomeRelativeFilePath(homeRelativePath))
        {
            throw new RepoProvisioningException(
                $"conversation path '{homeRelativePath}' is not a plain $HOME-relative path.");
        }

        var agentStore = AgentStorePath(vmRoot, repoHash, agentId);
        foreach (var segment in homeRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            agentStore = Path.Combine(agentStore, segment);
        }

        return agentStore;
    }

    /// <summary>
    /// True when <paramref name="source"/> is a path inside SOME <c>conversations/</c> tree — the one
    /// structural fact <see cref="ContainerSpecBuilder"/> can check without knowing the VM root (it is
    /// pure and takes no configuration). A whole-SEGMENT test, not a substring one:
    /// <c>/home/mainguard/mainguard/repos-conversations-backup</c> contains the text and is not a store.
    /// </summary>
    public static bool IsInsideAConversationTree(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var segments = source.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The marker must be a strict ANCESTOR — a directory literally named "conversations" is not
        // itself a mountable store.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], ConversationsDirectoryName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---- The invariant: a conversation path may never reach a credential -------------------------

    /// <summary>
    /// Whole-segment containment between two <c>$HOME</c>-relative paths, in <b>either</b> direction:
    /// true when they are equal, when <paramref name="a"/> contains <paramref name="b"/>, or when
    /// <paramref name="b"/> contains <paramref name="a"/>.
    ///
    /// <para>Direction matters both ways and neither is theoretical. A declared <c>.claude</c> CONTAINS
    /// <c>.claude/.credentials.json</c> (the accident this exists to stop), while a declared
    /// <c>.claude/projects</c> would be CONTAINED by a hypothetical credential declaration of
    /// <c>.claude</c>. Segment-wise, so <c>.claude-backup</c> is not inside <c>.claude</c>.</para>
    /// </summary>
    public static bool Overlaps(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var left = a.Trim().Trim('/');
        var right = b.Trim().Trim('/');
        return string.Equals(left, right, StringComparison.Ordinal)
               || left.StartsWith(right + "/", StringComparison.Ordinal)
               || right.StartsWith(left + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every (conversation, credential) pair that overlaps, in declaration order. Empty means the
    /// declaration is safe. Pure, so both the manifest parser and the spawn path ask the same question of
    /// the same implementation rather than inventing two.
    /// </summary>
    public static IReadOnlyList<(string ConversationPath, string CredentialPath)> FindCredentialOverlaps(
        IEnumerable<string>? conversationPaths, IEnumerable<string>? credentialPaths)
    {
        var conversations = conversationPaths?.ToArray() ?? Array.Empty<string>();
        var credentials = credentialPaths?.ToArray() ?? Array.Empty<string>();
        if (conversations.Length == 0 || credentials.Length == 0)
        {
            return Array.Empty<(string, string)>();
        }

        var found = new List<(string, string)>();
        foreach (var conversation in conversations)
        {
            foreach (var credential in credentials)
            {
                if (Overlaps(conversation, credential))
                {
                    found.Add((conversation, credential));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The hard refusal. Throws <see cref="ConversationStoreOverlapException"/> when any declared
    /// conversation path overlaps any declared credential path — see that type for why this is a typed
    /// failure rather than a filter, a warning or a doc note.
    /// </summary>
    public static void AssertNoCredentialOverlap(
        string adapterId, IEnumerable<string>? conversationPaths, IEnumerable<string>? credentialPaths)
    {
        var overlaps = FindCredentialOverlaps(conversationPaths, credentialPaths);
        if (overlaps.Count == 0)
        {
            return;
        }

        var first = overlaps[0];
        throw new ConversationStoreOverlapException(adapterId, first.ConversationPath, first.CredentialPath)
        {
            All = overlaps,
        };
    }

    /// <summary>
    /// The declared conversation paths that are actually usable, refusing the whole declaration if any of
    /// them could reach a credential. Two shape rules beyond the overlap gate:
    /// <list type="bullet">
    ///   <item>each path passes the single relative-path gate every manifest-sourced in-jail path goes
    ///   through (<see cref="Adapters.AdapterManifest.IsHomeRelativeFilePath"/>) — no absolute path, no
    ///   <c>~</c>, no <c>..</c> escape;</item>
    ///   <item>no declared path may contain another. Nested bind mounts under one another are an ordering
    ///   question nobody should have to reason about, and the nesting is always a mistake anyway — the
    ///   inner one is already covered by the outer.</item>
    /// </list>
    /// </summary>
    public static ImmutableArray<string> UsablePaths(
        string adapterId, IEnumerable<string>? conversationPaths, IEnumerable<string>? credentialPaths)
    {
        var declared = (conversationPaths ?? Array.Empty<string>())
            .Where(Adapters.AdapterManifest.IsHomeRelativeFilePath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (declared.Length == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        AssertNoCredentialOverlap(adapterId, declared, credentialPaths);

        for (var i = 0; i < declared.Length; i++)
        {
            for (var j = i + 1; j < declared.Length; j++)
            {
                if (Overlaps(declared[i], declared[j]))
                {
                    throw new ConversationStoreException(
                        "<manifest>",
                        $"adapter '{adapterId}' declares conversation paths '{declared[i]}' and "
                        + $"'{declared[j]}', one of which contains the other. Declare the outer path only — "
                        + "nested stores would mount one bind mount inside another.");
                }
            }
        }

        return declared.ToImmutableArray();
    }

    // ---- The in-jail probe: one exec, one pure parser ---------------------------------------------

    /// <summary>Frame opener for the store probe's verdict.</summary>
    private const string ProbeFrame = "MGCONV[";

    /// <summary>Verdict: every declared store is present and the agent uid can create a file in it.</summary>
    private const string ProbeOk = "OK";

    /// <summary>Verdict prefix: a store's mount point does not exist in the container at all.</summary>
    private const string ProbeMissing = "MISSING:";

    /// <summary>Verdict prefix: the mount point exists but the agent uid cannot write to it.</summary>
    private const string ProbeUnwritable = "UNWRITABLE:";

    /// <summary>
    /// The command run inside the started jail to prove it really has writable conversation stores.
    ///
    /// <para>The same discipline as <see cref="PackageCachePolicy.WritabilityProbe"/> and for the same
    /// reason: "the daemon asked for the mount" and "the container has a writable directory there" are
    /// different facts. Here the gap is worse than a failed build — a missing mount means the CLI writes
    /// its transcripts into the tmpfs again, everything looks fine for the whole session, and the loss is
    /// discovered only by the person who came back for the conversation.</para>
    ///
    /// <para>Sentinel-framed because an exit code alone lies: a dead container, a missing shell or a
    /// dropped transport all produce empty output, which a naive <c>Contains("OK")</c> reads as failure
    /// and a naive <c>!Contains("UNWRITABLE")</c> reads as PASS. A MISSING FRAME is its own reported
    /// reason ("the probe did not run"). The write test is a shell redirect and <c>rm</c> — no
    /// <c>mktemp</c>, no <c>touch</c> — so the probe cannot degrade into "tool missing" and report that as
    /// a policy verdict.</para>
    /// </summary>
    public static ImmutableArray<string> WritabilityProbe(IEnumerable<string> sandboxTargets)
    {
        var targets = sandboxTargets?.ToArray() ?? Array.Empty<string>();
        if (targets.Length == 0)
        {
            throw new ArgumentException("At least one sandbox target is required.", nameof(sandboxTargets));
        }

        var script =
            "for d in \"$@\"; do\n"
            + "  if [ ! -d \"$d\" ]; then printf '" + ProbeFrame + ProbeMissing + "%s]' \"$d\"; exit 0; fi\n"
            + "  f=\"$d/.mgconvprobe.$$\"\n"
            + "  if (: > \"$f\") 2>/dev/null; then rm -f \"$f\"; else "
            + "printf '" + ProbeFrame + ProbeUnwritable + "%s]' \"$d\"; exit 0; fi\n"
            + "done\n"
            + "printf '" + ProbeFrame + ProbeOk + "]'\n";

        var command = ImmutableArray.CreateBuilder<string>();
        command.Add("sh");
        command.Add("-c");
        command.Add(script);
        command.Add("sh");
        // Positional — never interpolated into script text.
        command.AddRange(targets);
        return command.ToImmutable();
    }

    /// <summary>
    /// Why the jail's conversation stores are not usable, or <c>null</c> when they are. Pure: the caller
    /// runs <see cref="WritabilityProbe"/> and hands the stdout here, so every branch is unit-assertable
    /// with no Docker and no container.
    /// </summary>
    public static string? DescribeProbeFailure(string? probeStdout, int exitCode)
    {
        var verdict = ReadFrame(probeStdout ?? string.Empty, ProbeFrame);

        if (verdict is null)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"the conversation-store probe produced no {ProbeFrame}…] frame (exit {exitCode}), so nothing about the mounts could be observed — the in-jail command did not run");
        }

        if (string.Equals(verdict, ProbeOk, StringComparison.Ordinal))
        {
            return null;
        }

        if (verdict.StartsWith(ProbeMissing, StringComparison.Ordinal))
        {
            return $"the container has no directory at '{verdict[ProbeMissing.Length..]}' — the bind mount is "
                   + "absent, so the CLI would write its transcripts into the 256 MiB tmpfs $HOME and lose them "
                   + "with the container, which is exactly the failure this store exists to remove";
        }

        if (verdict.StartsWith(ProbeUnwritable, StringComparison.Ordinal))
        {
            return $"'{verdict[ProbeUnwritable.Length..]}' exists in the container but the agent uid cannot "
                   + $"create a file in it — check that the conversations tree is group "
                   + $"'{UsernsRemapPolicy.JailGroupName}' (gid {UsernsRemapPolicy.AgentHostGid}) and "
                   + "group-writable, and that the mount is not read-only";
        }

        return $"the conversation-store probe reported an unrecognised verdict '{verdict}'";
    }

    /// <summary>Reads one sentinel-framed value. <c>null</c> means the frame was absent (the probe did
    /// not run); an empty string means it ran and said nothing, which is still a failure.</summary>
    private static string? ReadFrame(string output, string opener)
    {
        var start = output.IndexOf(opener, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += opener.Length;
        var end = output.IndexOf(']', start);
        return end < 0 ? null : output[start..end];
    }

    // ---- Guards ----------------------------------------------------------------------------------

    private static string RequireVmRoot(string vmRoot)
    {
        if (string.IsNullOrWhiteSpace(vmRoot))
        {
            throw new RepoProvisioningException(
                "The VM root is required to locate the conversation store directory.");
        }

        return vmRoot;
    }

    private static string RequireHandle(string repoHash)
    {
        if (string.IsNullOrEmpty(repoHash))
        {
            throw new RepoProvisioningException(
                "A repo hash is required to locate the conversation store directory.");
        }

        foreach (var c in repoHash)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                     || c is '.' or '_' or '-';
            if (!ok || repoHash.Contains("..", StringComparison.Ordinal))
            {
                throw new RepoProvisioningException(
                    $"Repo handle '{repoHash}' is not a usable single path component.");
            }
        }

        return repoHash;
    }
}
