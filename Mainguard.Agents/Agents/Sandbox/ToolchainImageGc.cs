using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// One toolchain image the garbage collector is judging.
/// </summary>
/// <param name="Id">The engine's content id (<c>sha256:…</c>) — the identity a removal names, because a
/// tag can be moved and an id cannot.</param>
/// <param name="Tags">Its <c>RepoTags</c>. Empty for a dangling image (a previous tag moved off it).</param>
/// <param name="Created">When the engine built it.</param>
/// <param name="ContainersUsing">How many containers reference it, <b>running or stopped</b>. Counted by
/// the caller from the container list rather than read off the image list, whose own <c>Containers</c>
/// field the engine leaves at <c>-1</c> unless it was explicitly asked to compute it.</param>
public sealed record ToolchainImageCandidate(
    string Id,
    IReadOnlyList<string> Tags,
    DateTimeOffset Created,
    int ContainersUsing);

/// <summary>Why one toolchain image was, or was not, removed.</summary>
public enum ToolchainImageGcDecision
{
    /// <summary>Nothing references it, nothing wants it, and newer layers already cover its purpose.</summary>
    Remove,

    /// <summary>A container references it. A stopped jail counts — <see cref="DockerSandboxEngine"/>
    /// reuses stopped jails, and reuse spawns the container from the image it already has.</summary>
    KeptInUse,

    /// <summary>The caller named it as still wanted (the tag this spawn just resolved).</summary>
    KeptWanted,

    /// <summary>Built too recently. A layer is built BEFORE the jail that will use it, so a young
    /// unreferenced layer is a spawn in flight, not garbage.</summary>
    KeptTooYoung,

    /// <summary>Inside the retained-newest window — kept so a base-image refresh does not strand the
    /// only copy of the layer a rollback would need.</summary>
    KeptRetained,

    /// <summary>Not a Mainguard toolchain layer at all. Never removed, whatever else is true.</summary>
    KeptForeign,
}

/// <summary>One image's verdict, with the sentence explaining it.</summary>
public sealed record ToolchainImageGcVerdict(
    ToolchainImageCandidate Image, ToolchainImageGcDecision Decision, string Reason)
{
    public bool Remove => Decision == ToolchainImageGcDecision.Remove;
}

/// <summary>
/// <b>Audit F32 — which stale toolchain layers may be deleted.</b>
///
/// <para>Every base-image refresh and every recipe edit changes
/// <see cref="ToolchainProvisioner.ImageTagFor(string, ToolchainDeclaration)"/>, so each one produces a
/// NEW 1–3 GB tag and leaves the previous one on disk forever. Nothing ever removed them, and on a
/// developer machine that is tens of gigabytes of layers for toolchains no jail can still be using.</para>
///
/// <para><b>The whole value of this type is what it refuses.</b> Deleting a layer a live jail is using
/// would break that agent's container in a way nothing else in the system can explain, so five
/// independent conditions each save an image on their own, and they are evaluated in that order — most
/// certain first, so the REASON reported is the strongest one that applied. It is pure, and it takes
/// the engine's facts as arguments, precisely so every one of those refusals is unit-assertable with no
/// Docker daemon involved.</para>
/// </summary>
public static class ToolchainImageGcPolicy
{
    /// <summary>Keep this many of the newest otherwise-removable layers. A base refresh rebuilds every
    /// declaration at once, and keeping a couple of generations means a revert does not re-download
    /// gigabytes.</summary>
    public const int DefaultRetain = 3;

    /// <summary>
    /// How old an unreferenced layer must be. Generous on purpose: the window this guards is "built,
    /// but the jail that will use it does not exist yet", and a spawn that builds a 2.9 GB .NET layer
    /// can spend many minutes between the two. Six hours costs nothing (the layer is deleted on the
    /// next sweep) and removes the entire class of racing a live spawn.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(6);

    /// <summary>True when <paramref name="tag"/> names the toolchain image repository.</summary>
    public static bool IsToolchainTag(string? tag) =>
        !string.IsNullOrEmpty(tag)
        && tag.StartsWith(ToolchainProvisioner.ImageName + ":", StringComparison.Ordinal);

    /// <param name="keep">Refs the caller still wants — tags, ids, or digests. Compared against both the
    /// candidate's tags and its id, because a resolved layer is carried around as a digest.</param>
    /// <param name="retain">How many of the newest otherwise-removable layers to keep anyway.</param>
    public static IReadOnlyList<ToolchainImageGcVerdict> Judge(
        IReadOnlyList<ToolchainImageCandidate> candidates,
        IReadOnlyCollection<string> keep,
        DateTimeOffset now,
        TimeSpan minimumAge,
        int retain = DefaultRetain)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(keep);

        var wanted = new HashSet<string>(keep.Where(k => !string.IsNullOrEmpty(k)), StringComparer.Ordinal);
        var verdicts = new Dictionary<string, ToolchainImageGcVerdict>(StringComparer.Ordinal);

        // Pass one: everything except the retained-newest window, which needs to know which images
        // survived the earlier rules before it can pick "the newest of what is left".
        var removable = new List<ToolchainImageCandidate>();
        foreach (var image in candidates)
        {
            var verdict = JudgeOne(image, wanted, now, minimumAge);
            verdicts[image.Id] = verdict;
            if (verdict.Remove)
            {
                removable.Add(image);
            }
        }

        // Pass two: spare the newest N of what pass one would delete.
        foreach (var spared in removable.OrderByDescending(i => i.Created).Take(Math.Max(0, retain)))
        {
            verdicts[spared.Id] = new ToolchainImageGcVerdict(spared, ToolchainImageGcDecision.KeptRetained,
                $"among the {Math.Max(0, retain)} newest removable layers, kept so a revert does not rebuild");
        }

        // Candidate order is preserved so a caller's log reads in the order the engine listed them.
        return candidates.Select(i => verdicts[i.Id]).ToArray();
    }

    private static ToolchainImageGcVerdict JudgeOne(
        ToolchainImageCandidate image, HashSet<string> wanted, DateTimeOffset now, TimeSpan minimumAge)
    {
        ArgumentNullException.ThrowIfNull(image);

        // A dangling layer (no tags at all) is only ours if the caller found it under our repository, so
        // an untagged candidate is trusted; a TAGGED one must be tagged in our repository, and if it
        // carries even one foreign tag it is shared with something else and is not ours to delete.
        if (image.Tags.Count > 0 && !image.Tags.All(IsToolchainTag))
        {
            return new ToolchainImageGcVerdict(image, ToolchainImageGcDecision.KeptForeign,
                $"carries a tag outside '{ToolchainProvisioner.ImageName}': "
                + string.Join(", ", image.Tags.Where(t => !IsToolchainTag(t))));
        }

        if (image.ContainersUsing > 0)
        {
            return new ToolchainImageGcVerdict(image, ToolchainImageGcDecision.KeptInUse,
                $"{image.ContainersUsing} container(s) reference it (a stopped jail is a reusable jail)");
        }

        if (wanted.Contains(image.Id) || image.Tags.Any(wanted.Contains))
        {
            return new ToolchainImageGcVerdict(image, ToolchainImageGcDecision.KeptWanted,
                "named by the caller as still wanted");
        }

        var age = now - image.Created;
        if (age < minimumAge)
        {
            return new ToolchainImageGcVerdict(image, ToolchainImageGcDecision.KeptTooYoung,
                $"built {FormatAge(age)} ago, inside the {FormatAge(minimumAge)} grace — a layer is built "
                + "before the jail that uses it");
        }

        return new ToolchainImageGcVerdict(image, ToolchainImageGcDecision.Remove,
            $"unreferenced and {FormatAge(age)} old");
    }

    private static string FormatAge(TimeSpan age) => age.TotalHours < 1
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)age.TotalMinutes)}m")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours}h");
}
