namespace Mainguard.Agents.Agents.Toolchains;

/// <summary>
/// The fixed in-VM layout for user-installed language toolchains, and where they appear inside a jail.
///
/// <para><b>Why this is a sibling of <see cref="Adapters.AdapterPaths"/> and not a new idea.</b> The
/// agent-CLI channel already solved this exact shape: a curated, pinned, hash-verified payload that a
/// HUMAN chooses to install, landing in a daemon-owned directory on the VM, bind-mounted READ-ONLY into
/// every jail so a thing installed after the image was built is usable with no image rebuild (G-16
/// forbids a runtime <c>docker build</c>, and the base image is a CI/release artefact). Toolchains are
/// the same problem with a different payload, so they get the same layout rather than a second design.</para>
///
/// <para><b>Read-only in the jail is load-bearing.</b> One directory tree is shared by every agent on the
/// machine. If a jail could write it, agent A could replace the interpreter agent B's <i>verification</i>
/// runs under — the merge gate decided by another tenant, which is precisely the reasoning
/// <see cref="Sandbox.PackageCachePolicy"/> records for keeping package caches per-agent. Toolchains can
/// be shared because they are never written from a jail; package caches cannot, because they are.</para>
///
/// <para><b>Where the writes go instead.</b> A read-only interpreter still has to be able to install a
/// project's dependencies, so every write a package manager makes is redirected into the per-agent
/// package cache (<see cref="Sandbox.PackageCachePolicy.SandboxMount"/>) by the toolchain's own
/// environment — for Python that is <c>PYTHONUSERBASE</c> alongside the <c>PIP_CACHE_DIR</c> that policy
/// already sets. Measured in a real hardened container: with the toolchain mounted read-only, the rootfs
/// read-only and <c>$HOME</c> a 256 MiB tmpfs, <c>pip install pytest</c> succeeds and
/// <c>python -m pytest</c> runs.</para>
/// </summary>
public static class ToolchainPaths
{
    /// <summary>The VM-side toolchains root: one directory per installed toolchain id, plus
    /// <c>stage/</c> for in-flight payloads and <c>registry/</c> for install markers. Under the fixed VM
    /// user's home, exactly like <see cref="Adapters.AdapterPaths.VmRoot"/>.</summary>
    public const string VmRoot = "/home/mainguard/mainguard/toolchains";

    /// <summary>Where <see cref="VmRoot"/> appears inside every agent sandbox (READ-ONLY).</summary>
    public const string SandboxMount = "/opt/mainguard/toolchains";

    /// <summary>Staging dir for a payload being downloaded and checksum-verified.</summary>
    public const string VmStageDir = VmRoot + "/stage";

    /// <summary>One JSON marker per installed toolchain, written LAST — only after the toolchain has
    /// been proven to RUN. See <see cref="ToolchainChannel"/> for why a marker is never trusted on its
    /// own.</summary>
    public const string VmRegistryDir = VmRoot + "/registry";

    /// <summary>The VM directory one toolchain installs into.</summary>
    public static string VmInstallDir(string id, string root = VmRoot) => $"{root}/{id}";

    /// <summary>The scratch directory an install extracts into before it is swapped into place, so a
    /// failed or interrupted install can never leave a half-populated toolchain that probes green on
    /// some other day.</summary>
    public static string VmStagingInstallDir(string id, string root = VmRoot) => $"{root}/.{id}.incoming";

    /// <summary>Where one toolchain appears inside a jail.</summary>
    public static string SandboxInstallDir(string id) => $"{SandboxMount}/{id}";

    /// <summary>The install marker for one toolchain.</summary>
    public static string RegistryMarkerPath(string id, string root = VmRoot) => $"{root}/registry/{id}.json";

    /// <summary>The staging directory under an arbitrary root.</summary>
    public static string StageDir(string root = VmRoot) => $"{root}/stage";

    /// <summary>The registry directory under an arbitrary root.</summary>
    public static string RegistryDir(string root = VmRoot) => $"{root}/registry";
}
