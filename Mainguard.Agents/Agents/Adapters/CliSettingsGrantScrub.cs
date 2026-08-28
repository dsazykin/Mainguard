using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mainguard.Agents.Agents.Ipc;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>
/// Keeps ROLE-SCOPED tool grants out of the per-repo settings store, in both directions.
///
/// <para><b>The defect (D5b).</b> A CLI's settings file is a permission allowlist, and it is harvested out
/// of a human-attended jail into a per-repository host store that seeds every later jail for that
/// repository. On this machine that store held
/// <c>Bash(/opt/mainguard/ipc/mainguard-agent *)</c> — the COORDINATOR's shim — recorded when the owner
/// answered "yes, don't ask again" in a coordinator's terminal. It was then restored into every later
/// jail of that repo, workers included: one role's grant crossing into another role's jail, which is
/// precisely the boundary the launch-flag design was chosen to hold.</para>
///
/// <para><b>The rule, stated exactly.</b> <see cref="AgentIpcPaths.SandboxMount"/> is Mainguard's own
/// mount, and the grants for it are issued per jail and per role by the daemon at launch
/// (<c>SandboxAgentLauncher.ApplyShimPreApproval</c>, one absolute path, derived from the shim that jail
/// was actually given). Nothing a jail writes about that directory may therefore persist: a settings file
/// that crosses this boundary carries no string naming it, in either direction.</para>
///
/// <para><b>Both directions, and why.</b> Scrubbing only the harvest would fix nothing that already
/// happened — the poisoned entry is already on the owner's disk and would keep being restored until some
/// later attended stop overwrote it. Scrubbing the restore neutralises every stored file immediately,
/// with no migration; scrubbing the harvest stops the store re-acquiring one, and makes the store
/// self-heal on the next attended stop, since a harvested file REPLACES its stored copy.</para>
///
/// <para><b>Deny entries go too, and that is not a widening.</b> A rule naming the IPC mount is dropped
/// wherever it appears, including from a <c>deny</c> list. What replaces it is not "nothing" but the
/// daemon's own per-jail grant: exactly one absolute path, the shim that jail's role was given, and no
/// other. Mainguard is the authority on its own mount, and a persisted rule from an agent-writable file
/// cannot be treated as a boundary anyway — it is indistinguishable from one the agent wrote itself.</para>
///
/// <para><b>Fail closed.</b> A file that names the mount and cannot be parsed as JSON does not travel at
/// all. Refusing to carry a settings file costs a re-approval; carrying one whose contents cannot be read
/// is how a grant nobody can see survives.</para>
/// </summary>
public static class CliSettingsGrantScrub
{
    /// <summary>The one thing no persisted settings file may mention. Read from
    /// <see cref="AgentIpcPaths.SandboxMount"/> rather than spelled here, so the mount and its exclusion
    /// cannot be moved apart.</summary>
    public const string DaemonOwnedPathPrefix = AgentIpcPaths.SandboxMount;

    /// <summary>
    /// <paramref name="content"/> with every reference to the daemon-owned IPC mount removed, or
    /// <c>null</c> when the file must not cross at all.
    ///
    /// <para>A file that does not mention the mount is returned <b>byte-identical</b> — the overwhelmingly
    /// common case, and the one where any re-serialisation would be a gratuitous rewrite of the owner's
    /// own configuration.</para>
    /// </summary>
    public static byte[]? Scrub(byte[]? content)
    {
        if (content is not { Length: > 0 })
        {
            return content;
        }

        if (!Mentions(content))
        {
            return content;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(
                Encoding.UTF8.GetString(content),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception e) when (e is JsonException or ArgumentException or DecoderFallbackException)
        {
            // Names the mount, and we cannot see what it says about it. It does not travel.
            return null;
        }

        var scrubbed = Strip(root);
        if (scrubbed is null)
        {
            // The whole document was one reference to the mount. There is nothing left to carry.
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(scrubbed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The belt to the braces: whatever the walk did, what LEAVES here never names the mount. A
        // spelling the recursion failed to reach (a key shape a future vendor invents) must fail closed
        // rather than pass through as the one thing this function exists to remove.
        return Mentions(bytes) ? null : bytes;
    }

    /// <summary>True when these bytes name the daemon-owned IPC mount anywhere at all.</summary>
    public static bool Mentions(byte[]? content) =>
        content is { Length: > 0 }
        && Encoding.UTF8.GetString(content).Contains(DaemonOwnedPathPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Rebuilds <paramref name="node"/> without any string naming the mount, and without any property
    /// whose NAME names it. Returns null when the node itself is such a string — the caller then drops the
    /// array element or the property that held it.
    /// </summary>
    private static JsonNode? Strip(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var (name, value) in obj.ToArray())
                    {
                        if (name.Contains(DaemonOwnedPathPrefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (value is null)
                        {
                            result[name] = null;
                            continue;
                        }

                        if (Strip(value.DeepClone()) is { } kept)
                        {
                            result[name] = kept;
                        }
                    }

                    return result;
                }

            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var element in array.ToArray())
                    {
                        if (element is null)
                        {
                            result.Add((JsonNode?)null);
                            continue;
                        }

                        if (Strip(element.DeepClone()) is { } kept)
                        {
                            result.Add(kept);
                        }
                    }

                    return result;
                }

            case JsonValue value when value.TryGetValue<string>(out var text)
                                      && text.Contains(DaemonOwnedPathPrefix, StringComparison.Ordinal):
                return null;

            default:
                return node.DeepClone();
        }
    }
}
