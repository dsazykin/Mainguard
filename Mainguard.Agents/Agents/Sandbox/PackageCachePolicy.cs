using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// MG-43 — how the daemon hands a bind-mount source to an identity it cannot name.
///
/// <para>The jail's HOST-side identity is not knowable to the daemon: with MG-17's userns-remap in force
/// the agent runs as host uid/gid <see cref="UsernsRemapPolicy.AgentHostUid"/> (101000), and without it
/// the container's uid 1000 is host uid 1000. The daemon is unprivileged either way — it cannot chown
/// into the remapped range, and .NET gives it no way to chgrp at all — so there are exactly two
/// mechanisms by which a directory it owns can be made writable by that identity, and which one applies
/// is a property of the machine, not a preference.</para>
/// </summary>
public enum PackageCacheGrant
{
    /// <summary>
    /// The MG-17 grant: the cache root is group-owned by <see cref="UsernsRemapPolicy.JailGroupName"/>
    /// (gid <see cref="UsernsRemapPolicy.AgentHostGid"/>), so a setgid parent hands that group down to
    /// every directory created under it and group-write is all the jail needs. This is the shipped
    /// substrate, provisioned at boot by <see cref="UsernsRemapPolicy.MountOwnershipScript"/>, and the
    /// cache leaf stays <c>2775</c> — NOT writable by anything outside the group.
    /// </summary>
    SharedJailGroup,

    /// <summary>
    /// The grant of last resort, used when the cache root is <b>not</b> group-owned by the jail gid — a
    /// developer box, a CI runner, or any VM root the boot step never provisioned (the merge-queue
    /// end-to-end suite builds one per test under <c>/tmp</c>). There is no group the daemon can name
    /// that the jail is in, so the only remaining channel is the mode's "other" bits, and the cache LEAF
    /// alone becomes <c>2777</c>.
    ///
    /// <para><b>Why this is not a weakened security property.</b> The leaf is reachable from a jail only
    /// through that jail's own bind mount — no jail mounts the root or the per-repo directory, and inside
    /// a container <c>..</c> from a bind-mount root is the container's parent, not the host's. So the
    /// cross-tenant property this subsystem exists to hold (agent A cannot write agent B's cache) is
    /// unchanged: it rests on the mount topology, not on the mode. What the "other" bits add is reach for
    /// other <i>host</i> users, and on MainguardEnv there are none besides root, which is omnipotent
    /// regardless. On the machines where this rung is actually taken — a dev box, a CI runner — every
    /// other bind-mount source in the test (the worktree, the per-agent repo) is already owned by the
    /// same unprivileged test user.</para>
    ///
    /// <para>It is nevertheless <b>reported</b> on every spawn (<c>PackageCacheUsage.Describe()</c>)
    /// rather than taken quietly, because a production daemon that finds itself here has a boot step that
    /// did not run, and that is worth seeing in a log.</para>
    /// </summary>
    ModeOnly,
}

/// <summary>
/// MG-43 — the pure, IO-free heart of the daemon-owned package cache: where it lives on the VM, where
/// it appears in the jail, what a package manager is told to put in it, how big it may get, and how a
/// jail proves it really has one. Everything here is a constant or a string builder, so the whole policy
/// is unit-assertable with no VM, no Docker daemon and no container.
///
/// <para><b>The defect this closes.</b> #269 gave the jail a per-repo verification toolchain, so
/// <c>dotnet</c> exists — but the jail's <c>$HOME</c> is a <b>256 MiB tmpfs</b>
/// (<see cref="ContainerSpecBuilder.AgentHome"/>, and verified as a hard limit: <c>dd</c> of 400 MiB
/// stops at 256), while <c>Mainguard.slnx</c>'s NuGet closure measures <b>1.7 GB</b>. Every package
/// manager's default cache is under <c>$HOME</c>, so <c>dotnet test Mainguard.slnx</c> — the command in
/// <c>.mainguard/verify</c> — runs out of space partway through restore. Verification is the gate that
/// decides whether an agent's work may enter the merge queue, so this blocked the whole loop for any
/// repository with a real dependency closure (.NET, Node, Rust, Java — i.e. most of them).</para>
///
/// <para><b>Two non-solutions, refused on purpose.</b> Weakening the verify command is not available —
/// the gate is only as good as the command behind it. Redirecting the cache into <c>/workspace</c> is
/// not available either: it puts gigabytes of untracked files <i>inside the tree under verification</i>,
/// per agent, where an agent's <c>git add -A</c> commits them. Hence a separate, daemon-owned tree on
/// ext4, mounted <b>outside</b> both the worktree and <c>$HOME</c>.</para>
///
/// <para><b>Why the cache is PER AGENT and never shared.</b> A cache two agents can write is a
/// supply-chain path <i>between tenants</i>: agent A drops a poisoned assembly into
/// <c>&lt;packages&gt;/newtonsoft.json/13.0.3/lib/…</c>, and because NuGet treats a package directory
/// carrying its <c>.nupkg.metadata</c> completion marker as already-installed — it does not re-hash the
/// extracted content — agent B's <i>verification</i> then compiles and runs against A's bytes. That is
/// not a cache bug, it is the merge gate being decided by another tenant. MG-36 gave every agent its own
/// network segment against exactly this class of reasoning, and "they are all the same user's agents" is
/// no more true here than it was there: the whole point of the sandbox is that an agent may be
/// prompt-injected. So the cardinality is the one MG-3 already established for
/// <see cref="ContainerSpecRequest.AgentRepoPath"/> — one directory, mounted into exactly one jail.</para>
///
/// <para><b>The two sharing designs that were considered and rejected.</b>
/// <list type="number">
///   <item><b>A read-only shared lower with a per-agent overlay upper.</b> Not implementable here in
///   either direction: mounting an overlay <i>inside</i> the jail needs <c>CAP_SYS_ADMIN</c>, which
///   <see cref="ContainerSpecBuilder"/> drops (<c>CapDrop ALL</c> plus <c>no-new-privileges</c>), and
///   mounting it <i>host-side</i> needs a privileged daemon, which MG-17 is specifically about not
///   being — <c>mainguardd</c> runs unprivileged as uid 1000.</item>
///   <item><b>A daemon-populated read-only fallback folder</b> (NuGet's <c>fallbackPackageFolders</c>,
///   which is the ecosystem's own answer to this shape). Mechanically fine, and it would deduplicate —
///   but populating it means the <i>daemon</i> runs restore, i.e. evaluates an agent-writable
///   repository's MSBuild files and fetches what they name, on the host side of every sandbox boundary
///   this subsystem exists to maintain. Moving untrusted-input processing out of the jail to save disk
///   is a strictly worse trade, so it is refused.</item>
/// </list>
/// The cost of per-agent caches is duplication, and duplication is answered with a budget and an
/// eviction policy (<see cref="DefaultBudgetBytes"/>, <c>PackageCacheManager</c>) rather than with a
/// shared writable surface.</para>
///
/// <para><b>MG-3 is not undone.</b> The cache is daemon-owned, but the mount is the <i>leaf</i>
/// <c>&lt;vmRoot&gt;/caches/&lt;repoHash&gt;/&lt;agentId&gt;</c> — never the root, never the per-repo
/// directory. A jail therefore cannot traverse to another agent's cache (inside the container,
/// <c>..</c> from a bind-mount root is the container's parent, not the host's), and the daemon's own
/// state under <c>~/.mainguard</c> is as unreachable as it was before. The tree also holds nothing the
/// daemon ever reads back to make a decision: it is derived, disposable content that only the jail
/// consumes. <see cref="ContainerSpecBuilder"/> enforces the shape structurally — a package-cache mount
/// source that is not inside a <c>caches/</c> tree is a typed refusal, so this mount can never be edited
/// into a second writable path at the mirror.</para>
///
/// <para><b>MG-17 ownership.</b> Nothing here chowns anything. <c>caches/</c> is provisioned
/// <c>2775 mainguard:mainguard-jail</c> by <see cref="UsernsRemapPolicy.MountOwnershipScript"/>, exactly
/// as <c>repos/</c>, <c>worktrees/</c> and <c>agents/</c> are, so every directory created underneath
/// inherits gid <see cref="UsernsRemapPolicy.AgentHostGid"/> and the setgid bit by construction. The
/// daemon (uid 1000, in the <c>mainguard-jail</c> group) and the remapped jail (host uid 101000, gid
/// 101000) then share the tree by group. The daemon cannot chown into the remapped range and does not
/// try.</para>
///
/// <para><b>Egress.</b> Nothing here widens it. Restore runs <i>inside</i> the jail, through the same
/// default-deny tinyproxy on the same allowlist, and the hosts it needs — <c>api.nuget.org</c> and
/// <c>www.nuget.org</c> for .NET, <c>registry.npmjs.org</c>, <c>pypi.org</c> +
/// <c>files.pythonhosted.org</c>, <c>crates.io</c> + <c>static.crates.io</c>,
/// <c>proxy.golang.org</c> — are already <see cref="EgressAllowlist.DefaultEntries"/>, as
/// <c>PackageRegistry</c> entries, which take the direct CONNECT route rather than the P2-08 model
/// gateway. Zero allowlist entries are added by this change. (The toolchain <i>tarball</i> host,
/// <c>builds.dotnet.microsoft.com</c>, stays off the allowlist: #269 deliberately fetches it at image
/// build time on the VM's network, which is why the toolchain is a layer and not an in-jail install.)</para>
/// </summary>
public static class PackageCachePolicy
{
    /// <summary>The <c>&lt;vmRoot&gt;</c> child holding every per-agent package cache. A sibling of
    /// <c>repos/</c>, <c>worktrees/</c> and <c>agents/</c>, and named in
    /// <see cref="UsernsRemapPolicy.MountOwnershipScript"/> alongside them.</summary>
    public const string CachesDirectoryName = "caches";

    /// <summary>
    /// Where the per-agent cache appears inside the jail.
    ///
    /// <para>Three properties are load-bearing and all three are asserted by
    /// <see cref="ContainerSpecBuilder"/>: it is <b>not</b> under
    /// <see cref="ContainerSpecBuilder.WorkspaceTarget"/> (so nothing here can be committed by an agent's
    /// <c>git add -A</c>, and nothing here is part of what verification measures), it is <b>not</b>
    /// <see cref="ContainerSpecBuilder.AgentHome"/> or under it (so it does not consume the 256 MiB
    /// tmpfs this exists to get out of), and it is an absolute path. <c>/var/cache</c> is the FHS home
    /// for exactly this — "application cache data … can be deleted without data loss".</para>
    ///
    /// <para>The directory need not exist in the image: a bind mount's target is created during rootfs
    /// setup, before <c>ReadonlyRootfs</c> remounts it read-only — the same reason
    /// <c>/opt/mainguard/adapters</c> works while the base image never creates it.</para>
    /// </summary>
    public const string SandboxMount = "/var/cache/mainguard";

    /// <summary>
    /// The size ceiling for the WHOLE cache root, across every repo and agent — 16 GiB.
    ///
    /// <para>The arithmetic, from the measurement that motivated this: one .NET closure of Mainguard's
    /// own size is ~1.7 GB, and <c>CoordinatorLimits.MaxActiveWorkers</c> is 6, so a fully-loaded swarm
    /// on this repository needs ~10 GB. 16 GiB leaves headroom for a second repo's worth of idle caches
    /// to survive between runs (which is the entire point — an evicted cache is a re-download) without
    /// letting derived data grow without bound on a VM whose disk the user did not size for it.</para>
    /// </summary>
    public const long DefaultBudgetBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>
    /// The smallest budget that is accepted at all — 4 GiB, about two closures.
    ///
    /// <para>A budget below this does not produce a smaller cache, it produces <i>thrashing</i>: every
    /// spawn evicts the cache the previous spawn just filled, so every verification re-downloads
    /// everything and the feature is strictly worse than not having it. A misconfiguration that turns a
    /// cache into a permanent cache miss is exactly the kind of quiet degradation this subsystem refuses
    /// elsewhere, so it is a typed construction failure instead.</para>
    /// </summary>
    public const long MinimumBudgetBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The daemon-written last-use marker's suffix. It is a SIBLING of the cache directory
    /// (<c>&lt;agentId&gt;.lastused</c> next to <c>&lt;agentId&gt;/</c>), never a file inside it, so the
    /// LRU ordering is daemon state that no jail can reach — the mount is the leaf directory alone, and
    /// its parent is never mounted anywhere. An agent cannot touch its own marker to dodge eviction.</summary>
    public const string LastUsedMarkerSuffix = ".lastused";

    // ---- Layout (pure) ---------------------------------------------------------------------------

    /// <summary><c>&lt;vmRoot&gt;/caches</c> — the root the budget is measured over. Never mounted.</summary>
    public static string CacheRoot(string vmRoot)
        => Path.Combine(RequireVmRoot(vmRoot), CachesDirectoryName);

    /// <summary><c>&lt;vmRoot&gt;/caches/&lt;repoHash&gt;</c> — every agent's cache for one repo. Never
    /// mounted (it holds the sibling last-use markers, which are daemon-only state).</summary>
    public static string RepoCacheDirectory(string vmRoot, string repoHash)
        => Path.Combine(CacheRoot(vmRoot), RequireHandle(repoHash));

    /// <summary><c>&lt;vmRoot&gt;/caches/&lt;repoHash&gt;/&lt;agentId&gt;</c> — the ONE directory this
    /// agent's jail mounts, and the only one it can see.</summary>
    public static string AgentCachePath(string vmRoot, string repoHash, string agentId)
    {
        // PROBE M3 (DO NOT MERGE): the per-agent segment is dropped, so EVERY agent working on this repo
        // is handed the SAME cache directory — the cross-tenant supply-chain path this feature exists to
        // close. The agent id is still validated so the failure mode is purely the shared path.
        _ = AgentRepoLayout.RequireAgentId(agentId);
        return Path.Combine(RepoCacheDirectory(vmRoot, repoHash), "shared");
    }

    /// <summary>The daemon-only last-use marker for one agent's cache (see
    /// <see cref="LastUsedMarkerSuffix"/>).</summary>
    public static string LastUsedMarkerPath(string vmRoot, string repoHash, string agentId)
        => AgentCachePath(vmRoot, repoHash, agentId) + LastUsedMarkerSuffix;

    /// <summary>
    /// True when <paramref name="source"/> is a path inside SOME <c>caches/</c> tree, which is the one
    /// structural fact <see cref="ContainerSpecBuilder"/> can check without knowing the VM root (the
    /// builder is pure and takes no configuration). It is deliberately a whole-SEGMENT test, not a
    /// substring one: <c>/home/mainguard/mainguard/repos-caches-backup</c> contains the text and is not
    /// a cache.
    /// </summary>
    public static bool IsInsideACacheTree(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var segments = source.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The last segment is the agent id; the cache marker must be a strict ANCESTOR, so a directory
        // literally named "caches" is not itself a mountable cache.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], CachesDirectoryName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---- The ownership grant (pure decision; the IO lives in PackageCacheManager) -----------------

    /// <summary>
    /// Which grant applies, given the group id the cache ROOT actually carries on disk.
    ///
    /// <para>The root's gid is the one observable that answers the question. MG-17 provisions
    /// <c>caches/</c> as <c>2775 mainguard:mainguard-jail</c>, so a root whose gid IS
    /// <see cref="UsernsRemapPolicy.AgentHostGid"/> proves both that the group exists and that the daemon
    /// can reach the jail through it (boot adds the daemon to that group in the same step, and the
    /// setgid bit propagates it to every child). Any other gid means that provisioning did not happen for
    /// THIS root, and no amount of group-permission setting by an unprivileged daemon will reach the
    /// jail.</para>
    ///
    /// <para><paramref name="cacheRootGid"/> is negative when the gid could not be read at all. That
    /// resolves to <see cref="PackageCacheGrant.ModeOnly"/> — the rung that works everywhere — because
    /// the alternative is a jail that cannot write its cache and a spawn that refuses, i.e. an
    /// unreadable stat taking the whole product down. The choice is reported either way.</para>
    /// </summary>
    public static PackageCacheGrant DecideGrant(int cacheRootGid)
        => cacheRootGid == UsernsRemapPolicy.AgentHostGid
            ? PackageCacheGrant.SharedJailGroup
            : PackageCacheGrant.ModeOnly;

    /// <summary>
    /// The mode the per-agent cache LEAF carries under <paramref name="grant"/>: <c>2775</c> for
    /// <see cref="PackageCacheGrant.SharedJailGroup"/>, <c>2777</c> for
    /// <see cref="PackageCacheGrant.ModeOnly"/>. setgid in both cases, so whatever group the leaf has
    /// keeps propagating to the package directories the jail creates under it.
    /// </summary>
    public static UnixFileMode LeafMode(PackageCacheGrant grant)
        => ParentMode | (grant == PackageCacheGrant.ModeOnly
            ? UnixFileMode.OtherWrite
            : UnixFileMode.None);

    /// <summary>
    /// <c>2775</c> — the mode for the cache ROOT and the per-repo directory, under either grant. These
    /// are never mounted into any jail, so they need no "other" write bit even when the leaf does; the
    /// "other" read+execute they keep is only so the kernel can traverse to the leaf.
    /// </summary>
    public static UnixFileMode ParentMode =>
        UnixFileMode.SetGroup
        | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // ---- What a package manager is told (pure) ---------------------------------------------------

    /// <summary>
    /// The environment every jail carrying a cache gets, as <c>NAME=value</c> entries in a stable order.
    ///
    /// <para>Only <b>cache</b> knobs are set — locations a package manager fills with content it can
    /// re-fetch. Nothing here changes which packages resolve, which feed they come from, or how they are
    /// verified, and nothing here is a secret (<see cref="ContainerSpecBuilder"/> re-asserts G-13 over
    /// the finished environment either way).</para>
    ///
    /// <para><b>Why <c>NUGET_PACKAGES</c> is the one that matters for this repo.</b> That is the global
    /// packages folder — the extracted 1.7 GB. <c>NUGET_HTTP_CACHE_PATH</c> is the downloaded
    /// <c>.nupkg</c>/index cache and <c>NUGET_PLUGINS_CACHE_PATH</c> the credential-provider cache; both
    /// are small, but leaving either on the tmpfs <c>$HOME</c> reintroduces a share of the same
    /// exhaustion, and having them beside the packages folder is what makes a re-run cheap.</para>
    ///
    /// <para><b>Ecosystems deliberately NOT mapped:</b> Maven/Gradle (<c>jdk-21</c> is a JDK, not a build
    /// tool — there is no catalogued build tool whose knob to set), Bundler and Composer (their knobs
    /// name an <i>install</i> location, not a cache, so pointing them here would change where code is
    /// executed from rather than where downloads are kept). Those keep today's behaviour — the tmpfs
    /// <c>$HOME</c> — which is honest rather than half-configured; a repo that needs them should be a
    /// follow-up that maps them deliberately.</para>
    /// </summary>
    public static ImmutableArray<string> Environment(string sandboxMount = SandboxMount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxMount);
        var root = sandboxMount.TrimEnd('/');

        return ImmutableArray.Create(
            // .NET / NuGet — the one that unblocks `.mainguard/verify` for this repository.
            $"NUGET_PACKAGES={root}/nuget/packages",
            $"NUGET_HTTP_CACHE_PATH={root}/nuget/http-cache",
            $"NUGET_PLUGINS_CACHE_PATH={root}/nuget/plugins-cache",
            // Node.
            $"npm_config_cache={root}/npm",
            // Python.
            $"PIP_CACHE_DIR={root}/pip",
            // Go — the module cache and the build cache are separate knobs and both are large.
            $"GOMODCACHE={root}/go/mod",
            $"GOCACHE={root}/go/build",
            // Rust. CARGO_HOME rather than a narrower knob because cargo's registry index, .crate cache
            // and git checkouts hang off it and have no separate variables; the toolchain BINARIES stay
            // in the image layer (/opt/mainguard-toolchain/rust-stable), so this moves downloads only.
            $"CARGO_HOME={root}/cargo");
    }

    /// <summary>The variable names <see cref="Environment"/> sets, for assertions that want to check
    /// presence without pinning values.</summary>
    public static ImmutableArray<string> EnvironmentNames(string sandboxMount = SandboxMount)
        => Environment(sandboxMount).Select(e => e.Split('=', 2)[0]).ToImmutableArray();

    // ---- The in-jail probe: one exec, one pure parser ---------------------------------------------

    /// <summary>Frame opener for the writability probe's verdict.</summary>
    private const string ProbeFrame = "MGCACHE[";

    /// <summary>Verdict: the mount is present and the agent uid can create a file in it.</summary>
    private const string ProbeOk = "OK";

    /// <summary>Verdict: the mount point does not exist in the container at all.</summary>
    private const string ProbeMissing = "MISSING";

    /// <summary>Verdict: the mount point exists but the agent uid cannot write to it.</summary>
    private const string ProbeUnwritable = "UNWRITABLE";

    /// <summary>
    /// The command run inside the started jail to prove it really has a writable cache.
    ///
    /// <para><b>Why this exists at all.</b> "The daemon asked for the mount" and "the container has a
    /// writable directory there" are different facts. MG-42 already paid for conflating them once — its
    /// answer was to probe the toolchain in the worker's own jail before every verification, and this is
    /// the same check for the same reason. The failure being defended against is a jail whose cache
    /// mount silently is not there: restore then writes into the read-only rootfs (or, on a permissive
    /// engine, into an unmounted directory that is really the tmpfs) and dies in a way that reads like
    /// the repository is broken.</para>
    ///
    /// <para><b>Why the verdict is sentinel-framed.</b> An exit code alone lies here: <c>docker exec</c>
    /// against a dead container, a missing shell, or a transport that drops the output all produce
    /// nothing, and a naive <c>stdout.Contains("OK")</c> reads that as failure while
    /// <c>!stdout.Contains("UNWRITABLE")</c> reads it as PASS. The opener is printed by the shell itself
    /// and never by the thing under test, so a MISSING FRAME is a distinct, reported reason ("the probe
    /// did not run") rather than either verdict — the same framing discipline as
    /// <see cref="UsernsRemapPolicy.ProbeScript"/>. The three verdict tokens are mutually
    /// non-overlapping, so none can be read out of another.</para>
    ///
    /// <para>The write test is a shell redirect and <c>rm</c> — no <c>mktemp</c>, no <c>touch</c>, no
    /// coreutils assumption beyond <c>rm</c> — so the probe cannot degrade into "tool missing" and
    /// report that as a policy verdict.</para>
    /// </summary>
    public static ImmutableArray<string> WritabilityProbe(string sandboxMount = SandboxMount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxMount);
        var root = sandboxMount.TrimEnd('/');

        var script =
            "d='" + root + "'; "
            + $"s={ProbeMissing}; "
            + "if [ -d \"$d\" ]; then "
            + $"  s={ProbeUnwritable}; "
            + "  f=\"$d/.mgcacheprobe.$$\"; "
            + $"  if (: > \"$f\") 2>/dev/null; then rm -f \"$f\"; s={ProbeOk}; fi; "
            + "fi; "
            + $"printf '{ProbeFrame}%s]' \"$s\"";

        return ImmutableArray.Create("sh", "-c", script);
    }

    /// <summary>
    /// Why the jail's cache is not usable, or <c>null</c> when it is. Pure: the caller runs
    /// <see cref="WritabilityProbe"/> and hands the stdout here, so every branch is unit-assertable with
    /// no Docker and no container.
    /// </summary>
    public static string? DescribeProbeFailure(string? probeStdout, int exitCode, string sandboxMount = SandboxMount)
    {
        var verdict = ReadFrame(probeStdout ?? string.Empty, ProbeFrame);

        if (verdict is null)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"the cache probe produced no {ProbeFrame}…] frame (exit {exitCode}), so nothing about the mount at "
                + $"'{sandboxMount}' could be observed — the in-jail command did not run");
        }

        return verdict switch
        {
            ProbeOk => null,
            ProbeMissing => $"the container has no directory at '{sandboxMount}' — the bind mount is absent, so a "
                          + "restore would write into the read-only rootfs (or, worse, into the 256 MiB tmpfs) "
                          + "instead of the daemon-owned cache",
            ProbeUnwritable => $"'{sandboxMount}' exists in the container but the agent uid cannot create a file in "
                             + $"it — check that the cache tree is group '{UsernsRemapPolicy.JailGroupName}' "
                             + $"(gid {UsernsRemapPolicy.AgentHostGid}) and group-writable, and that the mount is "
                             + "not read-only",
            _ => $"the cache probe reported an unrecognised verdict '{verdict}' for '{sandboxMount}'",
        };
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
            throw new Git.Exceptions.RepoProvisioningException(
                "MG-43: the VM root is required to locate the package cache directory.");
        }

        return vmRoot;
    }

    private static string RequireHandle(string repoHash)
    {
        if (string.IsNullOrEmpty(repoHash))
        {
            throw new Git.Exceptions.RepoProvisioningException(
                "MG-43: a repo hash is required to locate the package cache directory.");
        }

        foreach (var c in repoHash)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                     || c is '.' or '_' or '-';
            if (!ok || repoHash.Contains("..", StringComparison.Ordinal))
            {
                throw new Git.Exceptions.RepoProvisioningException(
                    $"MG-43: repo handle '{repoHash}' is not a usable single path component.");
            }
        }

        return repoHash;
    }

    /// <summary>Formats a byte count for a log line without pulling in a formatting dependency.</summary>
    internal static string Bytes(long value)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double scaled = value;
        var unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{scaled:0.##} {units[unit]}");
    }

    /// <summary>Convenience for callers that need the list as <c>List&lt;string&gt;</c>.</summary>
    internal static List<string> EnvironmentList(string sandboxMount = SandboxMount)
        => Environment(sandboxMount).ToList();
}
