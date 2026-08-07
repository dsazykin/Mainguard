using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Mainguard.Server.Logging;

/// <summary>
/// The registry of proto fields that may ever carry a credential (G-13). Every field
/// commented <c>// SECRET</c> in <c>Mainguard.Protos/protos/**</c> MUST be registered
/// here by (message full name, field number). The logging formatter consults this
/// registry and NEVER emits a masked field's value, length, or prefix.
///
/// The reviewer grep (<c>grep -rn "SECRET" Mainguard.Protos/protos/</c>) and the
/// <c>SecretMaskCoversEverySecretField</c> test both hold this registry to the proto
/// comments. Adding a <c>// SECRET</c> field without registering it here is a bug.
/// </summary>
public static class SecretFieldMask
{
    // (message full name, field number) pairs. Message full names are the proto
    // fully-qualified names, e.g. "mainguard.v1.SpawnAgentRequest".
    private static readonly HashSet<(string Message, int Field)> Masked = new()
    {
        // agent.proto — SpawnAgentRequest.model_api_key
        ("mainguard.v1.SpawnAgentRequest", 4),
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

    /// <summary>True if this (message, field) is a registered secret.</summary>
    public static bool IsSecret(string messageFullName, int fieldNumber)
        => Masked.Contains((messageFullName, fieldNumber));

    /// <summary>
    /// Renders a proto message to a log-safe string: every registered secret field is
    /// replaced with a fixed <c>***</c> mask — no value, no length, no prefix leaks.
    /// Non-secret fields render their value. Used by the logging interceptor only.
    /// </summary>
    public static string Redact(IMessage message)
    {
        var descriptor = message.Descriptor;
        var parts = new List<string>();
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            string rendered;
            if (IsSecret(descriptor.FullName, field.FieldNumber))
            {
                rendered = "***";
            }
            else
            {
                var value = field.Accessor.GetValue(message);
                rendered = value switch
                {
                    null => "",
                    IMessage nested => Redact(nested),
                    // Repeated MESSAGE fields (e.g. SpawnAgentRequest.extra_env) must redact each
                    // element — the collection's own ToString would bypass the mask entirely.
                    string s => s,
                    System.Collections.IEnumerable list => RenderList(list),
                    _ => value.ToString() ?? "",
                };
            }

            parts.Add($"{field.Name}={rendered}");
        }

        return $"{descriptor.Name} {{ {string.Join(", ", parts)} }}";
    }

    private static string RenderList(System.Collections.IEnumerable list)
    {
        var items = new List<string>();
        foreach (var item in list)
        {
            items.Add(item switch
            {
                null => "",
                IMessage nested => Redact(nested),
                _ => item.ToString() ?? "",
            });
        }

        return $"[{string.Join(", ", items)}]";
    }
}
