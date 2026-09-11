using System;
using System.Collections.Generic;
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
///
/// <para><b>F45 — and why <see cref="Scrub"/> alone was not enough.</b> Everything except IPC-mount
/// strings was carried byte-identical. So the file that crossed between jails could carry
/// <c>permissions.defaultMode: "bypassPermissions"</c> (which makes the whole allowlist decoration),
/// <c>hooks</c> (commands the CLI runs at lifecycle events — a <c>SessionStart</c> hook executes before
/// the first prompt), <c>apiKeyHelper</c> (a command whose stdout becomes the model credential),
/// <c>statusLine</c> (a command on a timer), <c>env</c>, and <c>Bash(*)</c>. An agent in ANY attended
/// jail could write those, and every later worker of that repository booted with them — workers whose
/// terminals are input-locked, so nobody is watching. <see cref="CarryOnly"/> is the answer: an
/// allowlist of carried keys, applied in both directions for the same reason the mount scrub is.</para>
///
/// <para><b>Three entry points, deliberately.</b> <see cref="CarryOnly"/> is for the adapter's declared
/// <c>settingsPaths</c> — files whose schema is the permission allowlist. <see cref="Scrub"/> stays for
/// the settings files that sit under <c>credentialPaths</c> (gemini-cli's and qwen-code's
/// <c>settings.json</c>, parked there for the migration reason the manifest explains): their schema is
/// a different vendor's, and an allowlist written for claude-code's keys would silently drop the auth
/// selection those files exist to carry. They get the mount scrub and a size ceiling; that residual is
/// named in <c>AdapterCredentialPolicy</c> rather than papered over.
/// <see cref="StripExecutableConfig"/> is the third, for the credential files that are NOT settings-shaped
/// — <c>.claude.json</c> above all — and it is the INVERSE of <see cref="CarryOnly"/>: it removes the keys
/// that name a program and carries everything else through untouched.</para>
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

        // Decided on the PARSED document, not on the raw bytes. The first cut asked `Mentions(content)`
        // first and returned early when the bytes held no literal `/opt/mainguard/ipc` — and JSON has
        // more than one spelling of a slash. A settings file written as
        // `"Bash(\/opt\/mainguard\/ipc\/mainguard-agent *)"` (or with `\u002f`) contains no such
        // substring, passed through byte-identical, and the CLI's own parser read the grant straight
        // back out of it. The file is agent-writable in the jail, so the writer of that spelling is the
        // agent. Parsing first makes the walk see what the CLI will see; the raw test is kept only for
        // content that is not JSON at all, where it is the one question that can be asked.
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(
                Encoding.UTF8.GetString(content),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception e) when (e is JsonException or ArgumentException or DecoderFallbackException)
        {
            // Not JSON. If it names the mount we cannot see what it says about it, so it does not travel;
            // if it does not, it is none of this function's business.
            return Mentions(content) ? null : content;
        }

        var scrubbed = Strip(root);
        if (scrubbed is null)
        {
            // The whole document was one reference to the mount. There is nothing left to carry.
            return null;
        }

        if (JsonNode.DeepEquals(scrubbed, root) && !Mentions(content))
        {
            // Nothing was removed and the bytes name nothing: the owner's own file, byte-identical.
            return content;
        }

        var bytes = Encoding.UTF8.GetBytes(scrubbed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The belt to the braces: whatever the walk did, what LEAVES here never names the mount. A
        // spelling the recursion failed to reach (a key shape a future vendor invents) must fail closed
        // rather than pass through as the one thing this function exists to remove.
        return Mentions(bytes) ? null : bytes;
    }

    // ---- F45: the allowlist -----------------------------------------------------------------------

    /// <summary>
    /// The ONLY top-level keys a persisted CLI settings file may carry between jails.
    ///
    /// <para><b>This is a decision, not an inventory.</b> An unknown key is dropped precisely because it
    /// is unknown: the file is agent-writable inside the jail and is restored into every later jail of
    /// the repository, so a key nobody enumerated is a key nobody reviewed. The four kept here are
    /// declarative — they describe a preference or a permission — and none of them names a command, a
    /// program, a server or an environment.</para>
    ///
    /// <para>What this removes is enumerated once, in <see cref="ExecutableConfigKeys"/>, and this list is
    /// its complement rather than a second description of it: the two are asserted disjoint by test, so a
    /// key cannot end up both carried here and stripped there (MG-12).</para>
    /// </summary>
    public static readonly IReadOnlyList<string> CarriedTopLevelKeys = new[]
    {
        "permissions", "model", "outputStyle", "includeCoAuthoredBy",
    };

    /// <summary>
    /// <b>The keys that name a program the CLI will execute.</b> The single place this codebase says which
    /// those are, read by both sides of the boundary: <see cref="CarryOnly"/> drops them by not listing
    /// them in <see cref="CarriedTopLevelKeys"/>, and <see cref="StripExecutableConfig"/> removes them by
    /// name. Two hand-maintained descriptions of one fact is the MG-12 failure this repository keeps
    /// re-finding, so there is one.
    ///
    /// <para>Why each one is not a hypothetical: <c>hooks</c> is a command run at CLI lifecycle events (a
    /// <c>SessionStart</c> hook executes before the first prompt is even shown); <c>apiKeyHelper</c> is a
    /// command whose stdout becomes the model credential; <c>statusLine</c> is a command run on a timer;
    /// <c>mcpServers</c> and <c>enableAllProjectMcpServers</c> name programs the CLI launches; <c>env</c>
    /// sets the environment the agent's own tools inherit. Every one of them is executable configuration
    /// that used to travel byte-identical out of one jail and into the next.</para>
    ///
    /// <para><b>None of them is a credential</b>, which is the whole reason the credential leg can strip
    /// them without a live account to test against: removing a key that names a program cannot change the
    /// answer to "is this user logged in".</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ExecutableConfigKeys = new[]
    {
        "mcpServers", "enableAllProjectMcpServers", "hooks", "apiKeyHelper", "statusLine", "env",
    };

    /// <summary>
    /// The keys carried from inside <c>permissions</c>. The rule lists themselves, and nothing else.
    ///
    /// <para><b><c>defaultMode</c> is the one that matters.</b> It is a single string that decides
    /// whether the CLI asks at all, and <c>bypassPermissions</c> turns the whole allowlist into
    /// decoration — so a jail that wrote it into <c>settings.local.json</c> was handing every later jail
    /// of that repository a prompt-free CLI, with the rule list still sitting there looking like a
    /// control. A restored file may describe WHICH commands are approved; it may not decide whether
    /// approval is asked for.</para>
    /// </summary>
    private static readonly string[] CarriedPermissionKeys =
    {
        "allow", "ask", "deny", "additionalDirectories",
    };

    /// <summary>
    /// <paramref name="content"/> reduced to the keys a settings file may carry between jails, or
    /// <c>null</c> when it must not cross at all.
    ///
    /// <para>This is <see cref="Scrub"/>'s stricter sibling and it subsumes it: the mount removal still
    /// applies (the allowlist keeps <c>permissions.allow</c>, and a rule naming Mainguard's own IPC
    /// mount is exactly a <c>permissions.allow</c> entry), on top of which everything outside the
    /// allowlist is dropped.</para>
    ///
    /// <para><b>Why an allowlist rather than a denylist of the dangerous keys.</b> A denylist is a bet
    /// that the reviewer thought of every executable key the vendor has shipped and every one it will
    /// ship. These files are vendor-defined, versioned by the vendor, updated inside the jail by an
    /// updater the jail runs, and read back by a CLI whose parser we do not own — so the bet is
    /// re-taken on every CLI release, silently, in the direction of "carried". The allowlist takes the
    /// same uncertainty and spends it the other way: a new key the vendor adds is dropped until someone
    /// looks at it, and the cost of being wrong is that the user re-sets a preference.</para>
    ///
    /// <para><b>Not JSON ⇒ it does not travel.</b> There is no way to allowlist the keys of a document
    /// that cannot be parsed, and "carry it unread" is the thing this function exists to stop.</para>
    /// </summary>
    public static byte[]? CarryOnly(byte[]? content)
    {
        if (content is not { Length: > 0 })
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
            return null;
        }

        // A settings file is an object. An array or a bare scalar at the root is not a document this
        // function can reason about key by key, so it carries nothing.
        if (root is not JsonObject obj)
        {
            return null;
        }

        var kept = new JsonObject();
        foreach (var (name, value) in obj.ToArray())
        {
            if (!CarriedTopLevelKeys.Contains(name, StringComparer.Ordinal) || value is null)
            {
                continue;
            }

            var carried = string.Equals(name, "permissions", StringComparison.Ordinal)
                ? CarryPermissions(value)
                : Strip(value.DeepClone());

            if (carried is not null)
            {
                kept[name] = carried;
            }
        }

        if (kept.Count == 0)
        {
            // Nothing carriable survived. An empty object would travel, be restored, and read to anyone
            // looking at the store like a settings file the user wrote — so nothing travels instead.
            return null;
        }

        if (JsonNode.DeepEquals(kept, root) && !Mentions(content))
        {
            // Nothing was dropped and the bytes name nothing: the owner's own file, byte-identical.
            return content;
        }

        var bytes = Encoding.UTF8.GetBytes(kept.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Mentions(bytes) ? null : bytes;
    }

    /// <summary>The <c>permissions</c> object reduced to its rule lists, with every rule that grants a
    /// whole tool unconditionally removed. Returns null when nothing survives, so the empty
    /// <c>"permissions": {}</c> is not carried either.</summary>
    private static JsonNode? CarryPermissions(JsonNode permissions)
    {
        if (permissions is not JsonObject obj)
        {
            return null;
        }

        var kept = new JsonObject();
        foreach (var (name, value) in obj.ToArray())
        {
            if (!CarriedPermissionKeys.Contains(name, StringComparer.Ordinal) || value is null)
            {
                continue;
            }

            if (Strip(value.DeepClone()) is not { } stripped)
            {
                continue;
            }

            // `deny` is a RESTRICTION, so its entries are carried as they are: dropping one would widen
            // what the next jail may do, which is the opposite of this function's job. `allow`/`ask` are
            // grants and are filtered.
            if (stripped is JsonArray rules && !string.Equals(name, "deny", StringComparison.Ordinal))
            {
                var bounded = new JsonArray();
                foreach (var rule in rules.ToArray())
                {
                    if (rule is null)
                    {
                        continue;
                    }

                    if (rule is JsonValue v && v.TryGetValue<string>(out var text) && IsUnbounded(text))
                    {
                        continue;
                    }

                    bounded.Add(rule.DeepClone());
                }

                stripped = bounded;
            }

            kept[name] = stripped;
        }

        return kept.Count > 0 ? kept : null;
    }

    /// <summary>
    /// True when a permission rule grants a whole tool rather than a named command — <c>Bash</c>,
    /// <c>Bash(*)</c>, <c>Bash(:*)</c>.
    ///
    /// <para>Such a rule is not an approval the user made about a command; it is the removal of the
    /// prompt for that tool, and it reads in a settings file exactly like the dozens of specific grants
    /// around it. A jail can write one; a human answering "yes, don't ask again" about
    /// <c>git status</c> cannot produce one. Dropping it costs a real user nothing — the specific
    /// grants they made are all bounded — and it removes the single line that turns an allowlist into
    /// an open door.</para>
    /// </summary>
    private static bool IsUnbounded(string rule)
    {
        var trimmed = rule.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            // A bare tool name ("Bash") is the whole tool. A bare "*" likewise.
            return true;
        }

        if (!trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            // Malformed: we cannot see what it names, so it does not travel.
            return true;
        }

        var pattern = trimmed[(open + 1)..^1].Trim();
        return pattern.Length == 0 || pattern is "*" or ":*" or "**";
    }

    // ---- F2: the credential leg — .claude.json and the programs it names --------------------------

    /// <summary>
    /// The top-level keys of the declared CREDENTIAL files that somebody has actually looked at. It exists
    /// only to keep <see cref="StripExecutableConfig"/>'s log quiet about the ordinary contents of those
    /// files, so that a key nobody has reviewed is the thing that stands out.
    ///
    /// <para><b>This list decides nothing.</b> Being absent from it changes no byte of what travels — an
    /// unreviewed key is carried exactly like a reviewed one. Being wrong here therefore costs a log line,
    /// never a login, which is why an inventory is acceptable in this one place and is not acceptable in
    /// <see cref="ExecutableConfigKeys"/>.</para>
    ///
    /// <para>Sources, in the order the adapters declare them: claude-code's <c>.claude.json</c> and
    /// <c>.claude/.credentials.json</c>, codex's <c>.codex/auth.json</c>, and gemini-cli's / qwen-code's
    /// <c>oauth_creds.json</c> and <c>google_accounts.json</c>. opencode's <c>auth.json</c> is keyed by
    /// PROVIDER ID rather than by a fixed schema, so its keys are expected to show up in the log; that is
    /// the benign noise this list cannot remove without guessing at provider names.</para>
    /// </summary>
    private static readonly HashSet<string> ReviewedCredentialKeys = new(StringComparer.Ordinal)
    {
        // .claude.json — session/telemetry state that accumulates with use.
        "numStartups", "installMethod", "autoUpdates", "theme", "hasCompletedOnboarding",
        "lastOnboardingVersion", "projects", "oauthAccount", "userID", "firstStartTime", "tipsHistory",
        "promptQueueUseCount", "subscriptionNoticeCount", "hasAvailableSubscription",
        "customApiKeyResponses", "fallbackAvailableWarningThreshold", "shiftEnterKeyBindingInstalled",
        "cachedChangelog", "changelogLastFetched", "lastReleaseNotesSeen", "isQualifiedForDataSharing",
        // .claude/.credentials.json
        "claudeAiOauth",
        // .codex/auth.json
        "OPENAI_API_KEY", "tokens", "last_refresh",
        // .gemini|.qwen/oauth_creds.json
        "access_token", "refresh_token", "id_token", "token_type", "scope", "expiry_date", "expiry",
        // .gemini|.qwen/google_accounts.json
        "active", "old",
    };

    /// <summary>
    /// <paramref name="content"/> with every key that names a program removed
    /// (<see cref="ExecutableConfigKeys"/>, at any depth) and <b>every other key carried untouched</b>, or
    /// <c>null</c> when the file must not cross at all.
    ///
    /// <para><b>Why a targeted strip here and an allowlist next door.</b> <see cref="CarryOnly"/> may drop
    /// an unknown key because the file it governs is a preference file: the cost of being wrong is that
    /// somebody re-sets a preference. This one governs <c>.claude.json</c>, which is where a CLI keeps the
    /// state that says the user is logged in — an allowlist that guessed one auth-bearing field wrong
    /// would lock the owner out of their own jails, and there is no way to prove it did not without a live
    /// logged-in account to test against. Inverting it removes that whole class of risk: the keys taken
    /// away are, by the definition in <see cref="ExecutableConfigKeys"/>, the ones that name a program,
    /// and a program name is not a credential. What survives is provable by test instead of by account.</para>
    ///
    /// <para><b>Both directions.</b> Harvest stops the vault re-acquiring these keys; restore neutralises
    /// the blobs already sitting in an owner's keychain from before this existed, with no migration —
    /// the same argument the mount scrub makes, for the same reason.</para>
    ///
    /// <para><b>Not JSON: carried unless it spells one of these keys.</b> This follows <see cref="Scrub"/>
    /// rather than <see cref="CarryOnly"/>, and deliberately. Refusing every unparseable credential file
    /// would refuse <c>.gemini/installation_id</c>, which is a bare UUID and is not JSON by design —
    /// costing a real login to protect a file that cannot contain the thing being removed. So content that
    /// cannot be parsed travels when it does not spell an executable key, and does not travel when it does:
    /// there we cannot see what it says, and carrying bytes nobody can read is how a program definition
    /// survives unseen. The quoted-key test that decides this is crude and can refuse an unparseable file
    /// that merely mentions one of the words — an acceptable trade, because a <c>.claude.json</c> that does
    /// not parse is already a file its own CLI cannot read.</para>
    ///
    /// <para><b><paramref name="unreviewedTopLevelKeys"/> is the answer to "a denylist rots".</b> A vendor
    /// that ships a NEW executable key would otherwise pass silently, so every top-level key nobody has
    /// reviewed (<see cref="ReviewedCredentialKeys"/>) is reported to the caller to log — the NAME and
    /// nothing else, already put through <see cref="SpellKeyForLog"/> so a caller cannot emit a name that
    /// is not a plain identifier. No value, no length, no prefix of a value ever leaves here.</para>
    ///
    /// <para><b>Deliberate non-goal.</b> The daemon-owned IPC mount is NOT scrubbed from these files. That
    /// is the settings leg's boundary (D5b) and this leg's schema is a different vendor's; widening the
    /// strip would blur what this function can promise, which is exactly "no key that names a program, and
    /// otherwise not one byte changed".</para>
    /// </summary>
    public static byte[]? StripExecutableConfig(byte[]? content, out IReadOnlyList<string> unreviewedTopLevelKeys)
    {
        unreviewedTopLevelKeys = Array.Empty<string>();
        if (content is not { Length: > 0 })
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
            return NamesAnExecutableKey(content) ? null : content;
        }

        if (root is JsonObject top)
        {
            unreviewedTopLevelKeys = top
                .Select(p => p.Key)
                .Where(k => !ReviewedCredentialKeys.Contains(k)
                            && !ExecutableConfigKeys.Contains(k, StringComparer.Ordinal))
                .Select(SpellKeyForLog)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var stripped = WithoutExecutableKeys(root);
        if (JsonNode.DeepEquals(stripped, root))
        {
            // Nothing named a program: the owner's own file, byte-identical. A credential file is the last
            // thing to re-serialise as a side effect — some of these are compared byte-for-byte by the
            // vault, and all of them belong to the user rather than to Mainguard.
            return content;
        }

        if (stripped is JsonObject { Count: 0 })
        {
            // The file was nothing but program definitions. An empty object would travel, be restored, and
            // read like a login file that is simply signed out.
            return null;
        }

        return Encoding.UTF8.GetBytes(stripped!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// A key name rendered safe to hand to a log sink: returned as written when it is a plain
    /// configuration identifier, and replaced wholesale otherwise.
    ///
    /// <para>A JSON object's key is chosen by whoever wrote the file, and inside a jail that can be the
    /// agent. The names a vendor ships are short identifiers; a secret is not one. Anything that is not a
    /// short identifier is therefore logged as a fixed placeholder, so the log still says "something
    /// unreviewed is in this file" without becoming a channel for the contents of it.</para>
    /// </summary>
    private static string SpellKeyForLog(string name) =>
        name.Length is > 0 and <= 40 && IsPlainIdentifier(name) ? name : "<non-identifier>";

    private static bool IsPlainIdentifier(string name)
    {
        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when these bytes spell one of <see cref="ExecutableConfigKeys"/> as a JSON key. Asked
    /// only of content that could not be parsed, where it is the one question that can be asked.</summary>
    private static bool NamesAnExecutableKey(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        return ExecutableConfigKeys.Any(k => text.Contains("\"" + k + "\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rebuilds <paramref name="node"/> without any property whose NAME is one of
    /// <see cref="ExecutableConfigKeys"/>, at any depth.
    ///
    /// <para>At any depth because that is where the real ones live: claude-code records a project's MCP
    /// servers under <c>projects.&lt;dir&gt;.mcpServers</c>, not at the root, so a top-level-only strip
    /// would have left F2's actual finding in place.</para>
    ///
    /// <para>Matched ordinally. JSON keys are case-sensitive and the CLI's own parser reads
    /// <c>mcpServers</c> and nothing else, so a differently-cased spelling is a key the CLI never acts
    /// on — and matching it loosely would drop keys that merely resemble one.</para>
    /// </summary>
    private static JsonNode? WithoutExecutableKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var (name, value) in obj.ToArray())
                    {
                        if (ExecutableConfigKeys.Contains(name, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        result[name] = value is null ? null : WithoutExecutableKeys(value.DeepClone());
                    }

                    return result;
                }

            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var element in array.ToArray())
                    {
                        result.Add(element is null ? null : WithoutExecutableKeys(element.DeepClone()));
                    }

                    return result;
                }

            default:
                return node?.DeepClone();
        }
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
