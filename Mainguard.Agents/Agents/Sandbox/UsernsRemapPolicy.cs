using System;
using System.Globalization;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// MG-17 — the ONE place that decides what "user-namespaced" means for MainguardEnv, and the pure
/// (IO-free, platform-independent) heart of enforcing it. Everything here is a constant or a string
/// builder so the whole policy is unit-assertable on Linux CI without a VM, a Docker daemon or a
/// container.
///
/// <para><b>The defect this closes.</b> The product claimed the jails were user-namespaced, but
/// <c>/etc/docker/daemon.json</c> set no <c>userns-remap</c> and <c>SandboxEngineOptions.UsernsMode</c>
/// defaulted to <c>""</c>. With no daemon-level remap, container uid 0 IS host uid 0 and container uid
/// 1000 IS the VM's <c>mainguard</c> user — the uid that owns the daemon, its keyring, its SQLite state
/// and every binary the jails execute. Every byte a jail wrote through a bind mount therefore landed
/// owned by the most privileged unprivileged identity in the VM.</para>
///
/// <para><b>The mapping.</b> dockerd is configured <c>"userns-remap": "mainguard"</c>, and
/// <c>/etc/subuid</c>+<c>/etc/subgid</c> pin that user's subordinate range to
/// <see cref="SubordinateBase"/>..<see cref="SubordinateBase"/>+<see cref="SubordinateCount"/>-1. Docker
/// maps container id <c>N</c> to host id <c>SubordinateBase + N</c>, so:
/// <list type="bullet">
///   <item>container root (0) → host <see cref="SubordinateBase"/> (100000) — owns nothing in the VM;</item>
///   <item>the agent CLI (1000) → host <see cref="AgentHostUid"/> (101000);</item>
///   <item>the supervisor uid (1001) → host 101001.</item>
/// </list>
/// The range is PINNED rather than discovered because the ownership of the bind-mount sources has to be
/// provisioned against a known number: a remap whose base we have to read back at runtime turns "are the
/// mounts usable?" into a second question with its own failure mode.</para>
///
/// <para><b>Why the mount sources are group-shared and NOT chowned to the remapped uid.</b> The obvious
/// reading of the finding ("chown mount sources to the remapped range") does not survive contact with
/// the code: the daemon CREATES those trees (<c>git clone --bare</c>, <c>git worktree add</c>) and keeps
/// writing them (incremental fetch, worktree removal, branch deletion, the merge-diff bridge), and it
/// runs unprivileged as uid 1000 — it can neither chown a tree to 101000 nor write one that is. So the
/// two jail-writable trees (<c>~/mainguard/repos</c>, <c>~/mainguard/worktrees</c>) stay OWNED by the
/// daemon and are shared with the jail through a group whose gid IS the remapped agent gid
/// (<see cref="JailGroupName"/> = <see cref="AgentHostGid"/>), with the setgid bit on directories so the
/// group propagates to everything created underneath. The daemon is added to that group; the jail is in
/// it by construction (its in-container gid 1000 remaps onto it).</para>
///
/// <para>That is a STRICTLY stronger posture than an owner-chown would have been: the jail can read and
/// write the content it legitimately needs, but it owns neither tree, so it cannot <c>chmod</c>,
/// <c>chown</c> or replace the directories themselves — and, crucially, it has no access at all to
/// anything ELSE under <c>/home/mainguard</c> (the daemon's keyring, tokens and database), which is
/// exactly what it did have while it ran as uid 1000.</para>
///
/// <para><b>By construction, for whoever comes next (MG-3).</b> The invariant is a property of the two
/// PARENT directories, not of each child: <c>repos/</c> and <c>worktrees/</c> are <c>2775
/// mainguard:mainguard-jail</c>, so ANY directory created inside them — including the per-agent
/// repositories MG-3 will add — inherits gid <see cref="AgentHostGid"/> and the setgid bit with no extra
/// work. The only thing a new directory must still do is make its CONTENT group-writable
/// (<see cref="GroupShareRecursive"/> in <c>WorktreeManager</c>, or <c>core.sharedRepository=group</c>
/// for a git dir), because the daemon's umask is 022 and umask is not inherited from the parent
/// directory. A tree MG-3 mounts READ-ONLY needs neither — read+traverse already come from the group.</para>
/// </summary>
public static class UsernsRemapPolicy
{
    /// <summary>The VM user whose subordinate id ranges dockerd remaps into. Naming the existing service
    /// user (rather than minting a <c>dockremap</c>) keeps the identity chain readable: one user owns the
    /// daemon and lends the container engine its subordinate ids. The user's OWN uid (1000) is NOT part
    /// of the mapping — Docker only ever uses its /etc/subuid range.</summary>
    public const string RemapUser = "mainguard";

    /// <summary>The first host id of the subordinate range (container id 0 lands here). 100000 is
    /// shadow's own default first allocation, so it never collides with a real account.</summary>
    public const int SubordinateBase = 100_000;

    /// <summary>How many ids the range covers — a full 65536, so every uid a container image can name
    /// is mappable and a container id outside it is a configuration error rather than a silent
    /// "nobody" (65534) fallback.</summary>
    public const int SubordinateCount = 65_536;

    /// <summary>The in-container uid the agent CLI runs as (mirrors <c>SandboxAgentLauncher.AgentUid</c>
    /// and the agent image's <c>agent</c> user).</summary>
    public const int AgentContainerUid = 1000;

    /// <summary>The HOST uid a jail's agent process actually has once remapped — the identity every
    /// write through a bind mount now lands as.</summary>
    public static int AgentHostUid => HostIdFor(AgentContainerUid);

    /// <summary>The HOST gid a jail's agent process actually has once remapped. This is the gid the
    /// shared trees are grouped to, and the gid <see cref="JailGroupName"/> is created with.</summary>
    public static int AgentHostGid => HostIdFor(AgentContainerUid);

    /// <summary>The host group whose gid IS <see cref="AgentHostGid"/>: the shared identity between the
    /// unprivileged daemon and every remapped jail. It exists so the grant is nameable and auditable
    /// (<c>ls -l</c> shows <c>mainguard-jail</c>, not a bare 101000).</summary>
    public const string JailGroupName = "mainguard-jail";

    /// <summary>
    /// The value <c>HostConfig.UsernsMode</c> carries so a container INHERITS the daemon's remap.
    ///
    /// <para>Docker's per-container knob is deliberately asymmetric and there is no value meaning
    /// "definitely remap this container": <c>""</c> means "whatever the daemon does" and
    /// <see cref="OptOutUsernsMode"/> means "opt this container OUT". So the empty string is the
    /// CORRECT value — but only once the daemon is actually configured, which is why the daemon-level
    /// fact is asserted by <see cref="DescribeUnsatisfied"/> at boot and not inferred from this constant.
    /// It is a named constant rather than a bare <c>""</c> so the intent is greppable and a future edit
    /// has to say what it means.</para>
    /// </summary>
    public const string InheritDaemonRemap = "";

    /// <summary>The one value that DISABLES the remap for a container (<c>docker run --userns=host</c>).
    /// <see cref="ContainerSpecBuilder"/> rejects it: it is the single edit that would silently give a
    /// jail host uids back while every other hardening flag still looked right.</summary>
    public const string OptOutUsernsMode = "host";

    /// <summary>Where the subordinate ranges are declared.</summary>
    public const string SubuidPath = "/etc/subuid";

    /// <summary>Where the subordinate ranges are declared.</summary>
    public const string SubgidPath = "/etc/subgid";

    /// <summary>The daemon-side root of the VM's mirrors/worktrees (<c>RepoProvisioner</c>'s default
    /// <c>vmRoot</c>, resolved to the fixed service-user home the tarball pins).</summary>
    public const string VmRoot = "/home/mainguard/mainguard";

    /// <summary>The exact content of <c>/etc/subuid</c> and <c>/etc/subgid</c>. Written whole (like
    /// <c>/etc/wsl.conf</c> and <c>/etc/docker/daemon.json</c>) so the file is a deterministic function
    /// of this policy and re-running the step converges instead of appending.</summary>
    public static string SubidFileContent =>
        string.Create(CultureInfo.InvariantCulture, $"{RemapUser}:{SubordinateBase}:{SubordinateCount}\n");

    /// <summary>The host id a container id maps to. Throws rather than returning a wrong number for an
    /// id outside the range — a silently out-of-range mapping is how a mount ends up owned by
    /// <c>nobody</c> with no error anywhere.</summary>
    public static int HostIdFor(int containerId)
    {
        if (containerId < 0 || containerId >= SubordinateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(containerId), containerId,
                $"MG-17: container id {containerId} is outside the remapped range 0..{SubordinateCount - 1}; "
                + "dockerd would map it to the overflow id (nobody), not to a usable host id.");
        }

        return SubordinateBase + containerId;
    }

    /// <summary>
    /// dockerd's storage root ONCE the remap is in effect. Docker relocates its whole data root to
    /// <c>&lt;root&gt;/&lt;uid&gt;.&lt;gid&gt;</c> when it remaps, which is (a) why the image store looks
    /// empty after the flip and (b) the single most direct observable proof that the remap in force is
    /// the one THIS policy configured, rather than merely "some remap".
    /// </summary>
    public static string DockerRootDir =>
        string.Create(CultureInfo.InvariantCulture, $"/var/lib/docker/{SubordinateBase}.{SubordinateBase}");

    /// <summary>The <c>docker info</c> security option present only when dockerd is remapping.</summary>
    public const string UsernsSecurityOption = "name=userns";

    // ---- The boot-time probe: one in-VM command, one pure parser --------------------------------

    /// <summary>Frame opener for the security-option list (see <see cref="DescribeUnsatisfied"/>).</summary>
    private const string SecurityOptionsFrame = "MGUSERNS[";

    /// <summary>Frame opener for dockerd's reported storage root.</summary>
    private const string RootDirFrame = "MGROOT[";

    /// <summary>Frame opener for the daemon user's group list.</summary>
    private const string GroupsFrame = "MGGROUPS[";

    /// <summary>
    /// The in-VM script whose stdout <see cref="DescribeUnsatisfied"/> parses. Three facts, each wrapped
    /// in its OWN sentinel frame, printed unconditionally.
    ///
    /// <para><b>The framing is the whole point.</b> A uid/gid assertion is exactly the shape of check
    /// that lies: <c>docker info</c> failing, the template being rejected, or the distro not answering
    /// all produce EMPTY output, and a naive <c>output.Contains("name=userns")</c> reads that as "not
    /// remapped" — indistinguishable from a real regression — while the mirror-image mistake
    /// (<c>!output.Contains("no userns")</c>) reads it as PASS. Because each frame's opener is printed by
    /// the shell itself and not by docker, a missing frame proves the probe did not run and is reported
    /// as its own distinct reason; a present-but-empty frame proves it ran and found nothing. The three
    /// openers are mutually non-overlapping, so no frame's text can be mistaken for another's (the
    /// <c>NOAAAA</c>-contains-<c>AAAA</c> class of bug).</para>
    /// </summary>
    public static string ProbeScript =>
        "d=$(docker info --format '"
        + SecurityOptionsFrame + "{{range .SecurityOptions}}{{.}};{{end}}]"
        + RootDirFrame + "{{.DockerRootDir}}]' 2>/dev/null || true); "
        + "g=$(id -nG " + RemapUser + " 2>/dev/null || true); "
        + "printf '%s" + GroupsFrame + "%s]' \"$d\" \"$g\"";

    /// <summary>
    /// Why the userns invariants are not met, or <c>null</c> when they all are. Pure: the caller runs
    /// <see cref="ProbeScript"/> and hands the stdout here, so every branch is unit-assertable with no
    /// VM, no Docker and no container.
    ///
    /// <para>Each condition names the observed value, because "userns is off" with no evidence is the
    /// diagnostic that costs an afternoon.</para>
    /// </summary>
    public static string? DescribeUnsatisfied(string? probeOutput)
    {
        var output = probeOutput ?? string.Empty;

        var securityOptions = ReadFrame(output, SecurityOptionsFrame);
        var rootDir = ReadFrame(output, RootDirFrame);
        var groups = ReadFrame(output, GroupsFrame);

        // The shell prints all three openers unconditionally, so a missing one means the probe itself
        // never ran (distro down, bash missing, the call failed) — NOT that the fact is false.
        if (groups is null)
        {
            return "the userns probe produced no output at all (no " + GroupsFrame + " frame), so nothing "
                 + "about the user-namespace remap could be observed — the in-distro command did not run";
        }

        if (securityOptions is null || rootDir is null)
        {
            return "`docker info` did not answer the userns probe (its " + SecurityOptionsFrame + "/"
                 + RootDirFrame + " frames are missing), so the remap could not be observed";
        }

        if (!securityOptions.Contains(UsernsSecurityOption, StringComparison.Ordinal))
        {
            return $"dockerd reports no user-namespace remapping — `docker info` SecurityOptions are "
                 + $"'{securityOptions}' and none is '{UsernsSecurityOption}', so container uid "
                 + $"{AgentContainerUid} is still host uid {AgentContainerUid} (the {RemapUser} user)";
        }

        // "Some remap" is not enough: the mount ownership is provisioned against THIS base, so a remap
        // with any other base leaves every bind mount unusable by the jail while docker info still says
        // name=userns. DockerRootDir carries the base verbatim.
        if (!string.Equals(rootDir, DockerRootDir, StringComparison.Ordinal))
        {
            return $"dockerd is remapped, but not to the range this VM is provisioned for: its storage root "
                 + $"is '{rootDir}' and userns-remap of '{RemapUser}' to {SubordinateBase} requires "
                 + $"'{DockerRootDir}' (check {SubuidPath}/{SubgidPath})";
        }

        if (!ContainsGroup(groups, JailGroupName))
        {
            return $"the '{RemapUser}' user is not a member of '{JailGroupName}' (gid {AgentHostGid}) — its "
                 + $"groups are '{groups}' — so the daemon could not read or write the very worktrees and "
                 + "mirrors it shares with the remapped jails";
        }

        return null;
    }

    /// <summary>Reads one sentinel-framed value: the text between <paramref name="opener"/> and the next
    /// <c>]</c>. <c>null</c> means the frame was absent (the probe did not run); an empty string means it
    /// ran and the value was empty — a distinction the caller depends on.</summary>
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

    /// <summary>Whole-token membership test over an <c>id -nG</c> list. A substring test would accept
    /// <c>mainguard-jail-readonly</c> (and, worse, would accept <c>mainguard</c> matching inside
    /// <c>mainguard-jail</c> if the arguments were ever swapped).</summary>
    private static bool ContainsGroup(string groups, string name)
    {
        foreach (var token in groups.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---- The provisioning scripts (pure builders; FirstBootStep runs them as root) ---------------

    /// <summary>
    /// Creates <see cref="JailGroupName"/> at gid <see cref="AgentHostGid"/> and puts the daemon user in
    /// it. Prints <c>MGCHANGED</c> exactly when it altered something, so the caller restarts
    /// <c>mainguardd</c> only when it has to — supplementary groups are fixed at process start, so a
    /// daemon that was already running when the group appeared would otherwise keep an identity that
    /// cannot touch the shared trees, with nothing anywhere saying so.
    /// </summary>
    public static string GroupProvisionScript =>
        "set -u; changed=0; "
        + $"if ! getent group {JailGroupName} >/dev/null 2>&1; then "
        + $"groupadd -g {AgentHostGid.ToString(CultureInfo.InvariantCulture)} {JailGroupName} || exit 1; changed=1; fi; "
        + $"if ! id -nG {RemapUser} 2>/dev/null | tr ' ' '\\n' | grep -qx {JailGroupName}; then "
        + $"usermod -aG {JailGroupName} {RemapUser} || exit 1; changed=1; fi; "
        + "if [ \"$changed\" = 1 ]; then printf 'MGCHANGED'; fi";

    /// <summary>The sentinel <see cref="GroupProvisionScript"/> prints when it actually changed the
    /// group membership (and therefore the daemon must be restarted to pick it up).</summary>
    public const string GroupChangedSentinel = "MGCHANGED";

    /// <summary>
    /// Brings the bind-mount sources to the shared-ownership invariant, idempotently.
    ///
    /// <para><b>repos/ + worktrees/</b> (bind-mounted READ-WRITE): group <see cref="JailGroupName"/>,
    /// group-rwX, setgid on every directory so new children inherit the group. Guarded on the top
    /// directory's own gid so the recursive pass runs ONCE — on an already-migrated VM this is three
    /// <c>stat</c>s, not a walk of every worktree on every boot.</para>
    ///
    /// <para><b>adapters/</b> (bind-mounted READ-ONLY): deliberately NOT group-shared. The jail's uid
    /// moved from "the owner" to "other" when the remap landed, so all it needs — and all it may have —
    /// is read+traverse, which <c>a+rX</c> guarantees for an npm tree whose modes came from whatever
    /// umask the install ran under. The mount is read-only anyway; keeping the shared CLI binaries out
    /// of the jail's group is the belt to that braces.</para>
    ///
    /// <para><b>~/.mainguard and the rest of the home directory are untouched.</b> That is the point of
    /// the change: the daemon's keyring, session token and SQLite stay reachable by uid 1000 alone.</para>
    /// </summary>
    public static string MountOwnershipScript(string vmRoot = VmRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmRoot);
        var gid = AgentHostGid.ToString(CultureInfo.InvariantCulture);
        return
            "set -u; "
            + $"root='{vmRoot}'; gid={gid}; "
            + "mkdir -p \"$root/repos\" \"$root/worktrees\" || exit 1; "
            // Owner only, and NEVER -R: a recursive chown would strip the remapped uid off every file
            // the jails legitimately wrote, on every boot. `mkdir -p` as root can create these three
            // root-owned on a fresh VM, which is the only ownership this has to repair.
            + $"chown {RemapUser}:{RemapUser} \"$root\" 2>/dev/null || true; "
            + $"chown {RemapUser} \"$root/repos\" \"$root/worktrees\" 2>/dev/null || true; "
            + "chmod 0755 \"$root\" || exit 1; "
            + "for d in \"$root/repos\" \"$root/worktrees\"; do "
            + "  cur=$(stat -c %g \"$d\" 2>/dev/null || echo -1); "
            + "  if [ \"$cur\" != \"$gid\" ]; then "
            + "    chgrp -R \"$gid\" \"$d\" || exit 1; "
            + "    chmod -R g+rwX \"$d\" || exit 1; "
            + "    find \"$d\" -type d -exec chmod g+s {} + || exit 1; "
            + "  fi; "
            + "  chmod 2775 \"$d\" || exit 1; "
            + "done; "
            + "if [ -d \"$root/adapters\" ]; then chmod -R a+rX \"$root/adapters\" || exit 1; fi";
    }

    /// <summary>
    /// Removes every mainguard container and network from the OLD (un-remapped) storage root before the
    /// remap is switched on.
    ///
    /// <para>Enabling <c>userns-remap</c> moves dockerd's whole data root, so the previous root's
    /// containers, images and networks become invisible rather than deleted: the jails and the shared
    /// egress proxy would linger as unmanaged state, and their bridges would keep holding subnets out of
    /// the 10.203/16 pool that the new root then allocates from. Running this while the old daemon is
    /// still up is the only moment they can be removed cleanly.</para>
    ///
    /// <para>Best effort by contract (every step is <c>|| true</c>): the migration must not be able to
    /// fail provisioning. The proxy IS recreated by this — once, at the flip — which is exactly the
    /// "acceptable once at migration time, never per spawn" case: after the flip the new root has no
    /// proxy container at all, so <c>EnsureReadyAsync</c>'s ordinary create path takes over and no
    /// running jail exists to be stranded (they were removed here).</para>
    /// </summary>
    public static string PreFlipDrainScript =>
        "set -u; "
        + "docker ps -aq --filter label=mainguard.role | xargs -r docker rm -f >/dev/null 2>&1 || true; "
        // …then by NAME as a plain substring. Deliberately NOT anchored with `^/`: Docker's
        // container-name filter has matched with and without the API's leading slash across versions,
        // and an anchor that silently stops matching would leave behind exactly the containers this
        // exists to remove. Network names, by contrast, carry no slash and are anchored below.
        + "docker ps -aq --filter name=mainguard- | xargs -r docker rm -f >/dev/null 2>&1 || true; "
        + "docker network ls -q --filter name=^mainguard- | xargs -r docker network rm >/dev/null 2>&1 || true; "
        + "exit 0";
}
