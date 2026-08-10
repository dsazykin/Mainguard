namespace GitLoom.Core.Models;

public class UserPreferences
{
    // Theme key from the app's theme catalog (see GitLoom.App/Themes);
    // unknown/legacy values fall back to the default theme at startup.
    public string Theme { get; set; } = "MidnightLoom";
    public bool EnableGlassmorphism { get; set; } = true;
    public string AutoDetectPath { get; set; } = string.Empty;
    public string LastOpenedRepoPath { get; set; } = string.Empty;
    public System.Collections.Generic.Dictionary<string, bool> SidebarExpandedStates { get; set; } = new System.Collections.Generic.Dictionary<string, bool>();
    public double SidebarWidth { get; set; } = 280;

    // Timeline View Options. Only settings the timeline actually applies live here — a persisted
    // preference nothing reads is a checkbox that lies to the user. (CompactReferencesView,
    // LongEdges and ReferencesOnTheLeft were exactly that and are gone; unknown keys left in an
    // existing settings file are simply ignored on load.)
    // Defaults are `true` because both were previously unconditional in the row template — the
    // toggles start where the timeline already was, so nothing changes for an existing user.
    public bool TagNames { get; set; } = true;
    public bool CommitTimestamp { get; set; } = true;

    // Timeline Column Options
    public bool ShowAuthorColumn { get; set; } = true;
    public bool ShowDateColumn { get; set; } = true;
    public bool ShowHashColumn { get; set; } = true;

    // Remotes / auto-fetch (T-10). Minutes between background fetches; 0 disables
    // auto-fetch entirely. The background AutoFetchService reads this each tick, so
    // a change takes effect on the next cycle without a restart.
    public int AutoFetchMinutes { get; set; } = 10;

    // Diff quality (T-13). Syntax highlighting in the diff/editor viewer; when false the editor
    // renders plain text (grammar unset). Persisted as JSON like the rest of UserPreferences.
    public bool SyntaxHighlightDiffs { get; set; } = true;

    // Commit & tag signing (T-15). When SignCommits is on, the commit/tag path switches to the
    // git CLI so git orchestrates gpg/ssh signing from the (locally written) repo config.
    // GpgFormat is "openpgp" (default) or "ssh"; SigningKey is the gpg key id/fingerprint or an
    // SSH public-key path; GpgProgram optionally overrides the gpg/ssh binary git invokes. These
    // are written to LOCAL repo config only — never global.
    public bool SignCommits { get; set; } = false;
    public string GpgFormat { get; set; } = "openpgp";
    public string SigningKey { get; set; } = string.Empty;
    public string? GpgProgram { get; set; }

    // Pre-commit safety scanner (T-30). When PreCommitScanEnabled is on, the staging panel scans the
    // staged change before a commit lands and gates on any blocker (secret / merge marker) behind an
    // explicit "Commit anyway". PreCommitMaxFileMB is the LargeFile threshold. JSON-persisted, no migration.
    public bool PreCommitScanEnabled { get; set; } = true;
    public int PreCommitMaxFileMB { get; set; } = 5;

    // Conventional-commit composer (T-31). When on, the staging panel shows the structured
    // conventional-commit composer (type/scope/description/body/breaking/co-authors/closes) and the
    // commit uses its assembled message; when off, the plain commit-message box is used. The commit
    // still routes through the existing commit path + the T-30 pre-commit scan. JSON-persisted, no migration.
    public bool UseStructuredCommitComposer { get; set; } = false;

    // Timeline signature column (T-15). When on, the timeline batch-reads `%G?` for the visible
    // commits and shows a verified/signed/bad badge; when off no `%G?` cost is paid.
    public bool ShowSignatureStatus { get; set; } = false;

    // Command palette & shortcuts (T-18). Overrides on top of the built-in ShortcutMap defaults
    // (action id → gesture string, e.g. "commit" → "Ctrl+Enter"). An empty string clears a default.
    // Persisted as JSON like the rest of UserPreferences, so rebinds survive a restart without a migration.
    public System.Collections.Generic.Dictionary<string, string> ShortcutBindings { get; set; } = new System.Collections.Generic.Dictionary<string, string>();

    // Timeline Highlight Options
    public bool HighlightMyCommits { get; set; } = true;
    public bool HighlightMergeCommits { get; set; } = false;
    public bool HighlightCurrentBranch { get; set; } = true;
    public bool HighlightNotCherryPickedCommits { get; set; } = false;

    // Settings screen (#78): which top-nav menus are pinned as their own icon button instead of
    // living inside the Collaborate/Tools flyouts. Default matches the issue's requested set.
    public System.Collections.Generic.List<string> PinnedMenuIds { get; set; } =
        new System.Collections.Generic.List<string> { "PullRequests", "Issues", "Notifications" };
}
