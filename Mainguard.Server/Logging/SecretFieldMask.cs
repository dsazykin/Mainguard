using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Mainguard.Server.Logging;

/// <summary>
/// Renders a proto message for the daemon's access log as an <b>op + sizes + ids</b> summary, never
/// as a body (F56).
///
/// <para><b>Why this is an allowlist.</b> This type used to render every field of every request and
/// response and mask six named ones. That is a denylist, and a denylist cannot keep up with new
/// message types: <c>ReadAudit</c>'s decrypted payloads, terminal scrollback rows, merge diffs,
/// verification logs, task prompts, chat text and plan text were all outside those six names, so
/// they were written verbatim into <c>~/.mainguard/logs/rpc.log</c> (5 MB × 3) and, under systemd,
/// into the journal. An API key pasted into a task prompt landed in a rolling file on disk. The
/// default is now the opposite: <b>a field's value is not rendered unless it is provably safe</b>.
/// Adding a new proto message, or a new field to an existing one, can no longer leak content by
/// omission — it leaks nothing until someone deliberately allowlists it.</para>
///
/// <para><b>What "provably safe" means.</b> Three rules, applied per field:</para>
/// <list type="bullet">
///   <item>A bounded scalar (bool, enum, integer, float) renders its value. A number cannot carry a
///   prompt or a credential, and counts/seqs/durations/status codes are the diagnostic value of an
///   access log. Registered secrets are excluded even here.</item>
///   <item>A string renders its value only if its <b>name</b> is in <see cref="LoggableNames"/> —
///   opaque handles and kind discriminators, not content — <b>and</b> the value is short and made of
///   identifier characters. Anything else renders as <c>&lt;str:LEN&gt;</c>: the length, never the
///   text, never a prefix.</item>
///   <item>Bytes, repeated fields, maps and nested messages never render content: bytes become
///   <c>&lt;bytes:LEN&gt;</c>, collections become their element count, nested messages recurse under
///   the same rules to a shallow depth cap.</item>
/// </list>
///
/// <para><b>The <c>// SECRET</c> registry survives as a second belt.</b> Every field commented
/// <c>// SECRET</c> in <c>Mainguard.Protos/protos/**</c> must still be registered here by (message
/// full name, field number), and <see cref="Summarize"/> refuses to render a registered field even
/// when the allowlist rules would have let it through. The reviewer grep
/// (<c>grep -rn "SECRET" Mainguard.Protos/protos/</c>) and the
/// <c>SecretFieldMask_ShouldCoverEverySecretProtoField</c> test hold the registry to the proto
/// comments; the allowlist means forgetting an entry is no longer a leak.</para>
/// </summary>
public static class SecretFieldMask
{
    /// <summary>Longest string value that may be rendered verbatim from an allowlisted field.</summary>
    private const int MaxLoggableStringLength = 64;

    /// <summary>How deep <see cref="Summarize"/> descends into nested messages before it stops.</summary>
    private const int MaxDepth = 2;

    // (message full name, field number) pairs. Message full names are the proto
    // fully-qualified names, e.g. "mainguard.v1.SpawnAgentRequest".
    private static readonly HashSet<(string Message, int Field)> Masked = new()
    {
        // agent.proto — SpawnAgentRequest.model_api_key
        ("mainguard.v1.SpawnAgentRequest", 4),
        // agent.proto — ResumeAgentRequest.model_api_key. A resume carries the same credentials a
        // spawn does, so it must mask the same things; its extra_env/cli_credentials are covered by
        // the two nested-message registrations below, exactly as SpawnAgentRequest's are.
        ("mainguard.v1.ResumeAgentRequest", 4),
        // agent.proto — EnvEntry.value (SpawnAgentRequest.extra_env: custom llm_env_* keys)
        ("mainguard.v1.EnvEntry", 2),
        // agent.proto — CliCredentialFile.content (CLI login state: SpawnAgentRequest.cli_credentials
        // in, StopAgentResponse.cli_credentials out)
        ("mainguard.v1.CliCredentialFile", 2),
        // agent.proto — CliSettingsFile.content. NOT a `// SECRET` field: a CLI's settings are
        // configuration, and their durable home is a plain per-repo JSON file rather than the OS
        // keychain. Masked anyway because the daemon has no business spraying a repository's
        // approved-command list through its log on every spawn, stop and harvest sweep — the registry
        // is one-directional (every SECRET field must be here; not everything here must be SECRET).
        ("mainguard.v1.CliSettingsFile", 3),
        // reposync.proto — ProvisionRepoRequest.credential_token
        ("mainguard.v1.ProvisionRepoRequest", 2),
    };

    /// <summary>
    /// The string field NAMES whose values may appear in a log line: opaque correlation handles and
    /// closed-set discriminators. Deliberately short, deliberately name-based rather than
    /// (message, field)-based — a new proto that names its correlation id <c>agent_id</c> gets the
    /// same treatment as every existing one, and a new proto that carries content gets nothing.
    ///
    /// <para>Nothing here is free text. A field whose value a human typed — a prompt, a title, a
    /// commit message, a chat line, a plan, a reason, a path — does not belong on this list, because
    /// the whole point of F56 is that user-typed text never reaches a log sink.</para>
    /// </summary>
    private static readonly HashSet<string> LoggableNames = new(StringComparer.Ordinal)
    {
        "agent_id",
        "agent_kind",
        "repo_handle",
        "repo_hash",
        "lease_id",
        "session_id",
        "request_id",
        "correlation_id",
        "task_id",
        "worker_id",
        "role",
        "kind",
        "state",
        "phase",
        "engine",
        "key_id",
    };

    /// <summary>True if this (message, field) is a registered secret.</summary>
    public static bool IsSecret(string messageFullName, int fieldNumber)
        => Masked.Contains((messageFullName, fieldNumber));

    /// <summary>
    /// The log-safe summary of <paramref name="message"/>: its type name, its wire size, the
    /// allowlisted ids and bounded scalars, and a count of everything withheld. No field content is
    /// ever rendered beyond those rules — no value, no length-plus-prefix, no first-N-characters.
    /// </summary>
    /// <example><c>SpawnAgentRequest{size=412B, repo_handle=8f21c0, agent_kind=claude-code, omitted=5}</c></example>
    public static string Summarize(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Summarize(message, depth: 0, includeSize: true);
    }

    private static string Summarize(IMessage message, int depth, bool includeSize)
    {
        var descriptor = message.Descriptor;
        var parts = new List<string>();
        if (includeSize)
        {
            parts.Add($"size={SafeSize(message)}B");
        }

        var omitted = 0;
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            var rendered = RenderField(message, descriptor, field, depth);
            if (rendered is null)
            {
                omitted++;
                continue;
            }

            parts.Add($"{field.Name}={rendered}");
        }

        if (omitted > 0)
        {
            parts.Add($"omitted={omitted}");
        }

        return $"{descriptor.Name}{{{string.Join(", ", parts)}}}";
    }

    /// <summary>The rendering for one field, or null when the field contributes nothing but a count
    /// to <c>omitted</c> (an unset scalar, or a value the rules refuse to render).</summary>
    private static string? RenderField(
        IMessage message, MessageDescriptor descriptor, FieldDescriptor field, int depth)
    {
        // Belt two: a registered `// SECRET` field is never rendered, whatever its name or type.
        if (IsSecret(descriptor.FullName, field.FieldNumber))
        {
            return null;
        }

        object? value;
        try
        {
            value = field.Accessor.GetValue(message);
        }
        catch
        {
            // A field the reflection accessor cannot read is not worth faulting a log line over.
            return null;
        }

        if (value is null)
        {
            return null;
        }

        // Maps and repeated fields: the element count, never the elements. A repeated message field
        // is exactly where bodies used to escape (extra_env, cli_credentials, audit records, diff
        // hunks, scrollback rows), so it renders a number and nothing else.
        if (field.IsMap)
        {
            return $"{{{CountOf(value)}}}";
        }

        if (field.IsRepeated)
        {
            return $"[{CountOf(value)}]";
        }

        switch (field.FieldType)
        {
            case FieldType.Bool:
            case FieldType.Enum:
            case FieldType.Int32:
            case FieldType.Int64:
            case FieldType.UInt32:
            case FieldType.UInt64:
            case FieldType.SInt32:
            case FieldType.SInt64:
            case FieldType.Fixed32:
            case FieldType.Fixed64:
            case FieldType.SFixed32:
            case FieldType.SFixed64:
            case FieldType.Float:
            case FieldType.Double:
                // Bounded scalars: a count, a seq, a duration, a status. Cannot carry a payload.
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

            case FieldType.String:
                var text = (string)value;
                if (text.Length == 0)
                {
                    return null; // unset — nothing to say
                }

                return IsLoggableIdentifier(field.Name, text) ? text : $"<str:{text.Length}>";

            case FieldType.Bytes:
                var bytes = (ByteString)value;
                return bytes.Length == 0 ? null : $"<bytes:{bytes.Length}>";

            case FieldType.Message:
            case FieldType.Group:
                if (value is not IMessage nested)
                {
                    return null;
                }

                // Nested messages recurse under the same rules, shallowly: past the cap a message is
                // its type name and its size, which is all an access log needs from it.
                return depth >= MaxDepth
                    ? $"<{nested.Descriptor.Name}:{SafeSize(nested)}B>"
                    : Summarize(nested, depth + 1, includeSize: false);

            default:
                return null;
        }
    }

    /// <summary>
    /// A string value may be rendered only when its field name is an allowlisted handle AND the value
    /// looks like a handle: short, and built from identifier characters. The value check is what stops
    /// an allowlisted name from becoming a hole when a caller stuffs content into it.
    /// </summary>
    private static bool IsLoggableIdentifier(string fieldName, string value)
    {
        if (!LoggableNames.Contains(fieldName) || value.Length > MaxLoggableStringLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = char.IsAsciiLetterOrDigit(c)
                || c is '-' or '_' or '.' or ':' or '/' or '@' or '+';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountOf(object value)
    {
        if (value is ICollection collection)
        {
            return collection.Count;
        }

        var count = 0;
        if (value is IEnumerable enumerable)
        {
            foreach (var _ in enumerable)
            {
                count++;
            }
        }

        return count;
    }

    private static int SafeSize(IMessage message)
    {
        try
        {
            return message.CalculateSize();
        }
        catch
        {
            return -1;
        }
    }
}
