using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The names a human has typed for individual agents, kept per repository.
///
/// <para><b>Why a name needs storing at all.</b> An agent's only built-in labels are its CLI kind and
/// its id: four <c>claude-code</c> sessions render four identical rows, and the id is 32 hex
/// characters. The rail now derives a name from the worker's brief where there is one
/// (<see cref="Mainguard.Agents.Agents.AgentInfo.DisplayName"/>), but a brief is the agent's own
/// account of its task and a person may simply want to call it something else. That choice has to
/// outlive a window close, so it lands on disk.</para>
///
/// <para><b>Why this shape.</b> It follows <see cref="CliSettingsStore"/> deliberately — an ordinary
/// JSON file under the Mainguard data root, one file per repository, atomically replaced. A name is
/// not a credential and not a secret; it is something the owner should be able to read, edit and throw
/// away, and "forget every name I gave agents in this repo" is then a single file they can delete.</para>
///
/// <para><b>Why keyed on the agent id, knowing ids are per-run.</b> The dock-layout store deliberately
/// refuses to key on an agent id, because a layout is meant to survive into the NEXT agent of the same
/// kind. A name is the opposite: it names <i>this</i> session, the one the person was looking at when
/// they typed it. Carrying it to a different agent would be attaching a human's label to work they
/// never saw. So the key is right, and the consequence — the entry outlives the agent — is handled by
/// deleting the entry when the agent is deleted, and by <see cref="Prune"/> for everything else.</para>
/// </summary>
public sealed class AgentNameStore
{
    /// <summary>The directory under the Mainguard data root that holds every repo's names.</summary>
    public const string DirectoryName = "agent-names";

    /// <summary>
    /// The longest name accepted. Long enough for a real sentence fragment ("rewrite the diff gutter"),
    /// short enough that a rail row stays a row: the value is rendered in a fixed-width sidebar, and a
    /// name that wraps to four lines makes the list harder to scan than the id it replaced.
    /// </summary>
    public const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _root;

    /// <summary>The production store: <c>&lt;data root&gt;/agent-names</c>.</summary>
    public AgentNameStore()
        : this(Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), DirectoryName))
    {
    }

    /// <summary>An explicit root — tests point this at a temp directory so nothing touches the
    /// user's real store.</summary>
    public AgentNameStore(string root) =>
        _root = root ?? throw new ArgumentNullException(nameof(root));

    /// <summary>Where this repository's names are kept. Public so a diagnostic (or the owner) can be
    /// told the exact file to look at or delete.</summary>
    public string FilePathFor(string repoHandle) =>
        Path.Combine(_root, CliSettingsStore.ScopeSegment(repoHandle) + ".json");

    /// <summary>
    /// Every name stored for this repository, by agent id; empty when there are none.
    ///
    /// <para>A missing, unreadable or corrupt file yields empty rather than throwing. The cost of that
    /// is the pre-feature behaviour — rows fall back to the derived name — which is a legible surface,
    /// never a broken one.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Load(string repoHandle)
    {
        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            // A blank scope is not a wildcard: answering with some other repository's names would put a
            // person's label for one agent onto an unrelated one.
            return EmptyNames;
        }

        try
        {
            var path = FilePathFor(repoHandle);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : EmptyNames;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return EmptyNames;
        }
    }

    /// <summary>This agent's stored name, or empty when the human has not named it.</summary>
    public string NameFor(string repoHandle, string agentId) =>
        Load(repoHandle).TryGetValue(agentId, out var name) ? name : string.Empty;

    /// <summary>
    /// Records <paramref name="name"/> for this agent, or CLEARS it when the name is blank — which is
    /// what makes "Reset name" and "rename to nothing" the same operation rather than two code paths
    /// that could disagree about what an empty box means.
    /// </summary>
    /// <returns>The name as stored (trimmed and capped), or empty when it was cleared. A failed write
    /// also answers empty, because nothing was stored.</returns>
    public string Save(string repoHandle, string agentId, string? name)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(agentId))
        {
            return string.Empty;
        }

        var cleaned = Clean(name);
        var stored = new Dictionary<string, string>(Load(repoHandle), StringComparer.Ordinal);
        if (cleaned.Length == 0)
        {
            if (!stored.Remove(agentId))
            {
                return string.Empty; // nothing was stored and nothing is being stored — no write.
            }
        }
        else
        {
            stored[agentId] = cleaned;
        }

        return Write(repoHandle, stored) ? cleaned : string.Empty;
    }

    /// <summary>Forgets this agent's name. Called when an agent is DELETED: the row is gone, and a
    /// name for a session that no longer exists is residue that would be copied forward forever by
    /// every later write.</summary>
    public void Forget(string repoHandle, string agentId)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        var stored = new Dictionary<string, string>(Load(repoHandle), StringComparer.Ordinal);
        if (stored.Remove(agentId))
        {
            Write(repoHandle, stored);
        }
    }

    /// <summary>
    /// Drops names for agents that are no longer in <paramref name="liveAgentIds"/>.
    ///
    /// <para>The store would otherwise only ever grow: agents are per-run, so every session a person
    /// names leaves an entry behind that nothing removes. This is the sweep that keeps the file the
    /// size of the fleet rather than the size of its history — called with the current listing, so
    /// "live" is measured against what the daemon actually reports rather than guessed from age.</para>
    /// </summary>
    /// <returns>How many names were dropped.</returns>
    public int Prune(string repoHandle, IReadOnlyCollection<string> liveAgentIds)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || liveAgentIds is null)
        {
            return 0;
        }

        var live = new HashSet<string>(liveAgentIds, StringComparer.Ordinal);
        var stored = new Dictionary<string, string>(Load(repoHandle), StringComparer.Ordinal);
        var stale = stored.Keys.Where(id => !live.Contains(id)).ToList();
        if (stale.Count == 0)
        {
            return 0;
        }

        foreach (var id in stale)
        {
            stored.Remove(id);
        }

        return Write(repoHandle, stored) ? stale.Count : 0;
    }

    /// <summary>
    /// A name as it will be stored: trimmed, control characters and newlines collapsed to spaces, and
    /// capped at <see cref="MaxNameLength"/>.
    ///
    /// <para>The newline collapse is not cosmetic. A name is rendered as a single row label, and a
    /// pasted multi-line string would either be clipped to its first line — silently storing something
    /// other than what the person sees — or push every row below it down the rail.</para>
    /// </summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var collapsed = new StringBuilder(name.Length);
        var lastWasSpace = false;
        foreach (var ch in name)
        {
            var c = char.IsControl(ch) || ch == ' ' ? ' ' : ch;
            if (c == ' ')
            {
                if (!lastWasSpace && collapsed.Length > 0)
                {
                    collapsed.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            collapsed.Append(c);
            lastWasSpace = false;
        }

        var cleaned = collapsed.ToString().TrimEnd();
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength].TrimEnd() : cleaned;
    }

    /// <summary>Parses a store file's text. Public so a round-trip test can assert on the exact bytes
    /// that land on disk rather than on this class's in-memory state.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return EmptyNames;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<StoredName[]>(stored);
            if (entries is null)
            {
                return EmptyNames;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.AgentId))
                {
                    continue;
                }

                // Re-cleaned on the way IN as well as out, so a hand-edited file cannot introduce a
                // name this app would never have written.
                var name = Clean(entry.Name);
                if (name.Length > 0)
                {
                    result[entry.AgentId] = name;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return EmptyNames;
        }
    }

    private bool Write(string repoHandle, IReadOnlyDictionary<string, string> names)
    {
        var path = FilePathFor(repoHandle);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (names.Count == 0)
            {
                // The last name for this repo was removed. Delete the file rather than leaving an empty
                // array behind, so the store's contents and the owner's view of it agree.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }

            var json = JsonSerializer.Serialize(
                names.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new StoredName(pair.Key, pair.Value))
                    .ToArray(),
                WriteOptions);

            // Stage-then-replace, as the settings store does: an interrupted write must not leave a
            // half-parsed file behind, and the reader treats a corrupt file as "no names" — which would
            // silently drop every name the person had given.
            var staging = path + ".partial";
            File.WriteAllText(staging, json, Encoding.UTF8);
            File.Move(staging, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a name write costs a re-type; throwing here would take down whatever lifecycle
            // action was being performed alongside it. Never the second.
            return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyNames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record StoredName(string AgentId, string Name);
}
