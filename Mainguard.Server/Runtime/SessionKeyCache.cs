using System;
using System.Collections.Concurrent;

namespace Mainguard.Server.Runtime;

/// <summary>
/// A memory-only cache of the model API keys the client has handed this daemon session, keyed by
/// <b>(repo handle, agent kind)</b>. The BYOK keystore is host-side (P2-01 — the daemon has no
/// keyring), so keys only ever arrive on <c>SpawnAgent</c>; a coordinator-initiated worker spawn (the
/// in-jail IPC channel) has no client in the loop and reuses the key last supplied for that repo and
/// kind. Never persisted, never logged (the value is write-only until consumed by the sandbox secret
/// injector), gone with the daemon process.
///
/// <para><b>MG-6.</b> This was previously keyed by <i>agent kind alone</i> and shared across every
/// repo and session for the daemon's process lifetime, so a coordinator's shim spawn could pull
/// whatever model key or harvested CLI OAuth files were last cached for that kind — potentially
/// supplied by a <i>different repo</i>. Scoping to the repo confines credentials to the repo they
/// were supplied for while preserving the intended reuse within it. A miss returns null and the
/// worker simply spawns without the credential (the CLI then takes its own unauthenticated path) —
/// the same behaviour as before, never a silent cross-repo substitution.</para>
/// </summary>
public sealed class SessionKeyCache
{
    private readonly ConcurrentDictionary<ScopedKey, string> _keys = new();
    private readonly ConcurrentDictionary<string, System.Collections.Generic.IReadOnlyDictionary<string, string>> _extraEnvByRepo =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ScopedKey, System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>>
        _cliCredentials = new();
    private readonly ConcurrentDictionary<ScopedKey, System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>>
        _cliSettings = new();

    /// <summary>Records the key supplied for <paramref name="agentKind"/> in <paramref name="repoHandle"/>
    /// (empty repo/kind/value are ignored).</summary>
    public void Remember(string repoHandle, string agentKind, string? modelApiKey)
    {
        if (TryScope(repoHandle, agentKind, out var scope) && !string.IsNullOrWhiteSpace(modelApiKey))
        {
            _keys[scope] = modelApiKey;
        }
    }

    /// <summary>The last key supplied for this repo + kind, or null when none was.</summary>
    public string? TryGet(string repoHandle, string agentKind) =>
        TryScope(repoHandle, agentKind, out var scope) && _keys.TryGetValue(scope, out var key) ? key : null;

    /// <summary>Records the custom env-var keys supplied on a client spawn (llm_env_* — they are not
    /// per-kind, but they ARE per-repo), so a coordinator-initiated worker spawn injects them too.</summary>
    public void RememberExtraEnv(string repoHandle, System.Collections.Generic.IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (!string.IsNullOrWhiteSpace(repoHandle) && extraEnv is { Count: > 0 })
        {
            _extraEnvByRepo[repoHandle] = extraEnv;
        }
    }

    /// <summary>The custom env-var keys last supplied for this repo, or null.</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? TryGetExtraEnv(string repoHandle) =>
        !string.IsNullOrWhiteSpace(repoHandle) && _extraEnvByRepo.TryGetValue(repoHandle, out var env) ? env : null;

    /// <summary>Records the CLI login-state files the client restored on a spawn of
    /// <paramref name="agentKind"/> in <paramref name="repoHandle"/>, so a coordinator-initiated worker
    /// of the same kind in the same repo (no client in the loop) boots logged in too. Memory-only, same
    /// lifetime rules as the model keys — the durable store is the host OS keychain, never the daemon.</summary>
    public void RememberCliCredentials(
        string repoHandle,
        string agentKind,
        System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? files)
    {
        if (TryScope(repoHandle, agentKind, out var scope) && files is { Count: > 0 })
        {
            _cliCredentials[scope] = files;
        }
    }

    /// <summary>The CLI login-state files last supplied for this repo + kind, or null.</summary>
    public System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? TryGetCliCredentials(
        string repoHandle, string agentKind) =>
        TryScope(repoHandle, agentKind, out var scope) && _cliCredentials.TryGetValue(scope, out var files) ? files : null;

    /// <summary>
    /// Records the CLI SETTINGS files — the user's approved-command list — for this repo + kind, so a
    /// coordinator-initiated worker (no client in the loop) inherits the approvals instead of
    /// re-prompting for every command the user already allowed.
    ///
    /// <para>The (repo, kind) scope is the same one the credentials use and it carries the same weight
    /// here for a different reason: a permission allowlist is a standing grant of execution, so one
    /// repo's approvals must never reach another repo's jail. A blank repo handle forms no scope and
    /// the write is simply dropped — never collapsed into a shared bucket.</para>
    /// </summary>
    public void RememberCliSettings(
        string repoHandle,
        string agentKind,
        System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>? files)
    {
        if (TryScope(repoHandle, agentKind, out var scope) && files is { Count: > 0 })
        {
            _cliSettings[scope] = files;
        }
    }

    /// <summary>The CLI settings files last supplied for this repo + kind, or null.</summary>
    public System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>? TryGetCliSettings(
        string repoHandle, string agentKind) =>
        TryScope(repoHandle, agentKind, out var scope) && _cliSettings.TryGetValue(scope, out var files) ? files : null;

    // A blank repo handle or kind can never form a scope — that would collapse distinct repos into one
    // shared bucket, which is exactly the MG-6 defect. Such calls are dropped (write) / miss (read).
    private static bool TryScope(string? repoHandle, string? agentKind, out ScopedKey scope)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(agentKind))
        {
            scope = default;
            return false;
        }

        scope = new ScopedKey(repoHandle, agentKind);
        return true;
    }

    /// <summary>The cache scope: credentials belong to one repo + one agent kind, never to a kind globally.</summary>
    private readonly record struct ScopedKey(string RepoHandle, string AgentKind);
}
