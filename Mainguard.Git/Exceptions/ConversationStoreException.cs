using System;
using System.Collections.Generic;

namespace Mainguard.Git.Exceptions;

/// <summary>
/// The daemon-owned conversation store a jail needs could not be prepared, mounted, or proven usable.
///
/// <para><b>Why this is an exception and not a fallback</b>, in the same register as
/// <see cref="PackageCacheException"/>. The store exists because the jail's <c>$HOME</c> is a 256 MiB
/// tmpfs that dies with the container, and the CLI's conversation transcripts live under it. A store
/// that is silently absent does not degrade the feature — it produces exactly the state the feature was
/// built to remove: an agent resumed onto its own branch whose CLI comes up with no memory of the work,
/// with nothing anywhere saying why. That failure is invisible until the moment a human needs the
/// history, which is the moment it is too late to notice.</para>
/// </summary>
public class ConversationStoreException : MainguardException
{
    public ConversationStoreException(string storeRoot, string detail)
        : base($"The daemon-owned conversation store under '{storeRoot}' is unusable: {detail}")
    {
        StoreRoot = storeRoot;
        Detail = detail;
    }

    /// <summary>The store root the failure is about (<c>&lt;vmRoot&gt;/conversations</c>).</summary>
    public string StoreRoot { get; }

    /// <summary>What actually went wrong.</summary>
    public string Detail { get; }
}

/// <summary>
/// The per-agent conversation store could not be created, or is not present/writable <i>inside the
/// container</i> once it started.
///
/// <para>The in-container half is its own fact and is measured on its own: the store's whole value is
/// that the CLI writes straight into daemon-owned ext4 as it goes, so a mount that is not really there
/// means the CLI is writing to the tmpfs again and the next crash takes everything with it — while the
/// daemon's log happily reports a store it prepared. Same conflation MG-42 and MG-43 each paid for
/// once.</para>
/// </summary>
public sealed class ConversationStoreUnavailableException : ConversationStoreException
{
    public ConversationStoreUnavailableException(string storeRoot, string agentStorePath, string detail)
        : base(storeRoot, $"the store for this agent at '{agentStorePath}' is unavailable — {detail}")
        => AgentStorePath = agentStorePath;

    /// <summary>The per-agent store directory that could not be made usable.</summary>
    public string AgentStorePath { get; }
}

/// <summary>
/// <b>The invariant that makes this feature safe to ship.</b> An adapter declared a conversation path
/// that contains — or is contained by — one of its own declared credential paths, so the spawn is
/// refused before any container exists.
///
/// <para><b>Why a refusal and not a filter.</b> The owner's standing rule is that logins and tokens live
/// only in the host OS keychain and are transferred into the jail at boot; nothing agent-side stores
/// them. A conversation store breaks that rule the moment it can hold one: it is daemon-owned ext4 that
/// deliberately OUTLIVES the jail, so a credential landing there is a token persisted to plain disk, in a
/// tree whose entire purpose is to survive teardown, remounted into every later jail for that agent id.
/// The specific accident this exists to stop is a manifest that declares <c>.claude</c> — which is where
/// the transcripts live, and which also CONTAINS <c>.claude/.credentials.json</c>.</para>
///
/// <para>Filtering the overlapping path out and continuing would be worse than refusing: the feature
/// would look configured and persist a subset nobody chose, and the manifest would keep its wrong
/// declaration. Prefix containment, not string equality, is the test — equality alone passes
/// <c>.claude</c> vs <c>.claude/.credentials.json</c>, which is the case that matters.</para>
/// </summary>
public sealed class ConversationStoreOverlapException : ConversationStoreException
{
    public ConversationStoreOverlapException(
        string adapterId, string conversationPath, string credentialPath)
        : base(
            "<manifest>",
            $"adapter '{adapterId}' declares the conversation path '{conversationPath}', which overlaps its "
            + $"declared credential path '{credentialPath}'. A conversation store is daemon-owned disk that "
            + "outlives the jail; a credential may only ever live in the host OS keychain. Narrow the "
            + "conversation path so it cannot contain the credential file "
            + "(e.g. '.claude/projects', never '.claude').")
    {
        AdapterId = adapterId;
        ConversationPath = conversationPath;
        CredentialPath = credentialPath;
    }

    /// <summary>The adapter whose declaration was refused.</summary>
    public string AdapterId { get; }

    /// <summary>The declared conversation path that overlapped.</summary>
    public string ConversationPath { get; }

    /// <summary>The declared credential path it overlapped with.</summary>
    public string CredentialPath { get; }

    /// <summary>Every overlap found, so a refusal names all of them rather than one at a time.</summary>
    public IReadOnlyList<(string ConversationPath, string CredentialPath)> All { get; init; } =
        Array.Empty<(string, string)>();
}
