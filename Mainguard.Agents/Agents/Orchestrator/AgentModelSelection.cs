using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>Which population a model choice applies to.</summary>
/// <remarks>
/// Two, not one, because the two are spent very differently: a coordinator plans and delegates across a
/// whole session while workers are many and short-lived, so "a cheap planner driving expensive workers"
/// and its reverse are both things an operator reasonably wants. One shared setting could express
/// neither.
/// </remarks>
public enum AgentModelRole
{
    /// <summary>The operator-facing coordinator CLI.</summary>
    Coordinator,

    /// <summary>Every daemon-spawned worker (a coordinator's subagents and external-PR jails).</summary>
    Worker,
}

/// <summary>One stored choice: which model a role's agents of a given CLI kind are launched with.</summary>
/// <param name="Role">Coordinator or worker.</param>
/// <param name="AgentKind">The adapter id the choice applies to, e.g. <c>claude-code</c>.</param>
/// <param name="Model">The model, exactly as it will be passed to that CLI. Empty is not stored — see
/// <see cref="AgentModelSelection.Set"/>.</param>
public sealed record AgentModelChoice(AgentModelRole Role, string AgentKind, string Model);

/// <summary>The persistence seam, mirroring <see cref="IPlanModeStore"/>.</summary>
public interface IAgentModelStore
{
    /// <summary>Every stored choice, or empty when nothing is persisted (or it is unreadable).</summary>
    IReadOnlyList<AgentModelChoice> Load();

    /// <summary>Persist the complete set, replacing what is there.</summary>
    void Save(IReadOnlyList<AgentModelChoice> choices);
}

/// <summary>An <see cref="IAgentModelStore"/> that forgets on restart. The default in tests.</summary>
public sealed class InMemoryAgentModelStore : IAgentModelStore
{
    private IReadOnlyList<AgentModelChoice> _choices;

    public InMemoryAgentModelStore(IReadOnlyList<AgentModelChoice>? seed = null) =>
        _choices = seed ?? Array.Empty<AgentModelChoice>();

    public IReadOnlyList<AgentModelChoice> Load() => _choices;

    public void Save(IReadOnlyList<AgentModelChoice> choices) => _choices = choices;
}

/// <summary>
/// A one-file JSON <see cref="IAgentModelStore"/>, written beside the plan-mode store for the same
/// reason that one is not an EF row: the daemon must be able to answer "which model does the next spawn
/// get?" before anything that needs a database is up, and a migration for a handful of strings buys
/// nothing.
///
/// <para><b>Every failure reads as "no choice stored", never as a wrong model.</b> A missing, truncated
/// or hand-mangled file leaves each CLI on its own default — which is exactly the behaviour before this
/// setting existed, and is the only safe direction: a corrupt file must not be able to silently redirect
/// an operator's spend to a model they did not pick.</para>
/// </summary>
public sealed class JsonAgentModelStore : IAgentModelStore
{
    private readonly string _path;

    public JsonAgentModelStore(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    private sealed record Entry(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("agentKind")] string AgentKind,
        [property: JsonPropertyName("model")] string Model);

    private sealed record Document(
        [property: JsonPropertyName("choices")] IReadOnlyList<Entry>? Choices);

    public IReadOnlyList<AgentModelChoice> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<AgentModelChoice>();
            }

            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (doc?.Choices is not { Count: > 0 } entries)
            {
                return Array.Empty<AgentModelChoice>();
            }

            var result = new List<AgentModelChoice>(entries.Count);
            foreach (var entry in entries)
            {
                // An unknown role spelling is DROPPED rather than defaulted to one of them: guessing
                // would apply a model to a population the operator never chose it for.
                if (entry is null
                    || !Enum.TryParse<AgentModelRole>(entry.Role, ignoreCase: true, out var role)
                    || string.IsNullOrWhiteSpace(entry.AgentKind)
                    || string.IsNullOrWhiteSpace(entry.Model))
                {
                    continue;
                }

                result.Add(new AgentModelChoice(role, entry.AgentKind.Trim(), entry.Model.Trim()));
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<AgentModelChoice>();
        }
    }

    public void Save(IReadOnlyList<AgentModelChoice> choices)
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        var doc = new Document(choices
            .Select(c => new Entry(c.Role.ToString(), c.AgentKind, c.Model))
            .ToList());
        File.WriteAllText(_path, JsonSerializer.Serialize(doc, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}

/// <summary>
/// The operator's <b>model choice per (role, CLI)</b>: which model a coordinator, and which model a
/// worker, is launched with.
///
/// <para><b>Why this is daemon-side, like <see cref="PlanModeSwitch"/>.</b> Workers are spawned by the
/// coordinator from inside its own jail, over the <c>mainguard-agent</c> shim — no client is in that
/// loop at all. A client-held preference would therefore be read by nothing on the path that actually
/// builds the launch line, which is the decorative-control shape this codebase keeps finding (MG-12).
/// The daemon owns the spawn, so the daemon owns the setting.</para>
///
/// <para><b>Read once per spawn.</b> Changing a model governs the NEXT agent and never an agent already
/// running: a live re-read could not move a model anyway (the CLI's process was launched with the flag),
/// so re-reading would only make the surface disagree with the jails.</para>
///
/// <para><b>No default, deliberately.</b> An unset choice means "whatever this CLI does on its own",
/// which is the behaviour that shipped before this existed. Mainguard picking a model on the operator's
/// behalf would be choosing how their money is spent.</para>
/// </summary>
public sealed class AgentModelSwitch
{
    /// <summary>The audit event a model change is recorded under.</summary>
    public const string ModelChangedEvent = "agent_model_changed";

    private readonly IAgentModelStore _store;
    private readonly IAuditLog _audit;
    private readonly object _gate = new();
    private Dictionary<(AgentModelRole Role, string Kind), string> _choices;

    public AgentModelSwitch(IAgentModelStore? store = null, IAuditLog? audit = null)
    {
        _store = store ?? new InMemoryAgentModelStore();
        _audit = audit ?? new InMemoryAuditLog();
        _choices = Index(_store.Load());
    }

    private static Dictionary<(AgentModelRole, string), string> Index(
        IReadOnlyList<AgentModelChoice> choices)
    {
        var map = new Dictionary<(AgentModelRole, string), string>(KeyComparer);
        foreach (var choice in choices)
        {
            map[(choice.Role, choice.AgentKind)] = choice.Model;
        }

        return map;
    }

    /// <summary>
    /// The model to launch a <paramref name="role"/> agent of <paramref name="agentKind"/> with, or
    /// empty when the operator has chosen none — in which case the CLI's own default stands and nothing
    /// is added to the launch line.
    /// </summary>
    public string ModelFor(AgentModelRole role, string? agentKind)
    {
        if (string.IsNullOrWhiteSpace(agentKind))
        {
            return string.Empty;
        }

        lock (_gate)
        {
            return _choices.TryGetValue((role, agentKind!.Trim()), out var model) ? model : string.Empty;
        }
    }

    /// <summary>Every stored choice, for the surface that renders them.</summary>
    public IReadOnlyList<AgentModelChoice> All()
    {
        lock (_gate)
        {
            return _choices
                .Select(pair => new AgentModelChoice(pair.Key.Role, pair.Key.Kind, pair.Value))
                .OrderBy(c => c.Role)
                .ThenBy(c => c.AgentKind, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Records a choice, or CLEARS it when <paramref name="model"/> is blank — returning the agent to
    /// its CLI's own default. One call for both, because "no model" is a real state an operator returns
    /// to, not an error.
    /// </summary>
    /// <returns>The model as stored, or empty when the choice was cleared.</returns>
    public string Set(AgentModelRole role, string agentKind, string? model)
    {
        if (string.IsNullOrWhiteSpace(agentKind))
        {
            return string.Empty;
        }

        var kind = agentKind.Trim();
        var cleaned = (model ?? string.Empty).Trim();
        string previous;

        lock (_gate)
        {
            _choices.TryGetValue((role, kind), out previous!);
            previous ??= string.Empty;

            if (cleaned.Length == 0)
            {
                _choices.Remove((role, kind));
            }
            else
            {
                _choices[(role, kind)] = cleaned;
            }

            _store.Save(_choices
                .Select(pair => new AgentModelChoice(pair.Key.Role, pair.Key.Kind, pair.Value))
                .ToList());
        }

        // Audited because it changes what every subsequent agent of that kind COSTS, and because a model
        // that silently changed under an operator is the kind of thing only a record can settle.
        _audit.Append(new AuditEvent(ModelChangedEvent, new Dictionary<string, string>
        {
            ["role"] = role.ToString(),
            ["agent_kind"] = kind,
            ["from"] = previous,
            ["to"] = cleaned,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        return cleaned;
    }

    /// <summary>Ordinal on the kind — adapter ids are exact identifiers, not display text.</summary>
    private static readonly IEqualityComparer<(AgentModelRole, string)> KeyComparer =
        new RoleKindComparer();

    private sealed class RoleKindComparer : IEqualityComparer<(AgentModelRole, string)>
    {
        public bool Equals((AgentModelRole, string) x, (AgentModelRole, string) y) =>
            x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((AgentModelRole, string) obj) =>
            HashCode.Combine(obj.Item1, StringComparer.Ordinal.GetHashCode(obj.Item2));
    }
}
