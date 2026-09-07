using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The two trust gates on the CLI-settings round trip — the security half of "stop making me
/// re-approve every command".
///
/// <para><b>What is being persisted.</b> A CLI's settings file is, in practice, a
/// <i>permission allowlist</i>: commands the user answered "yes, and don't ask again" to. Carrying it
/// between agents is the whole feature, and it is also the whole risk, because an inherited allowlist
/// is inherited <b>execution</b>. Two gates bound it, and each is asserted here against the shipped
/// <see cref="AgentSpawnService"/> resolved from the daemon's own container — not a hand-built copy of
/// the logic.</para>
///
/// <list type="number">
///   <item><b>IN — untrusted jails inherit nothing.</b> The external-PR intake spawns its worker with
///   <c>withoutHostCredentials: true</c>, and that flag now gates settings as well as logins. The
///   decisive case is not "the caller passed none" but "the caller passed none AND the daemon's
///   per-(repo, kind) fallback cache is warm": before this, an untrusted spawn with no explicit
///   settings would have picked the cached ones up and booted pre-approved to run whatever the user
///   had ever allowed in that repository.</item>
///   <item><b>OUT — only a human-attended jail's approvals flow back.</b> The settings file is
///   agent-writable by construction, so a harvest cannot distinguish "the user clicked approve" from
///   "the agent wrote the file". A <see cref="AgentRoles.Managed"/> worker's terminal is daemon-locked
///   read-only (P2-14) — nobody could have approved anything in it — so its settings are never
///   persisted. External-PR workers are Managed, so this gate covers them a second time.</item>
/// </list>
///
/// <para>Restore stays deliberately WIDER than harvest: a Managed worker still <i>receives</i> the
/// repo's approvals (otherwise it stalls on prompts nobody can answer), it just cannot write to them.
/// Grants flow in from a human-managed source and never back out of an unattended one, and the last
/// test pins exactly that asymmetry.</para>
/// </summary>
public sealed class CliSettingsBoundaryTests
{
    private const string AgentKind = "probe-cli";
    private const string RepoHandle = "settings-boundary-repo";

    /// <summary>The declared settings entry. Workspace-rooted because that is where the CLI actually
    /// records "don't ask again" grants, so the test rides the root that matters.</summary>
    private static readonly AdapterSettingsPath Declared = new("workspace", ".probe/settings.local.json");

    /// <summary>The grant found in the reporting machine's per-repo store: the coordinator's shim, spelled
    /// as claude-code records a "don't ask again" answer.</summary>
    private const string CoordinatorShimGrant =
        "Bash(" + AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.SpawnShimFileName + " *)";

    /// <summary>The adapter's declared login-state files. Two of them, because the F2 fix treats them
    /// differently: an ordinary token blob, and one that is a SETTINGS file sitting in this field —
    /// the shape gemini-cli and qwen-code ship (see <see cref="AdapterCredentialPolicy"/>).</summary>
    private static readonly string[] DeclaredCredentialPaths = { ".probe/auth.json", ".probe/settings.json" };

    private const string DeclaredCredentialPath = ".probe/auth.json";

    private const string DeclaredSettingsShapedCredentialPath = ".probe/settings.json";

    private const string InJailLogin = "{\"refresh_token\":\"rt-from-the-jail\"}";

    private static SandboxSettingsFile NewGrant(string command) => NewGrants(command);

    /// <summary>One stored settings file whose allowlist holds these commands — the shape the per-repo
    /// store actually has (one file per declared path, many rules inside it).</summary>
    private static SandboxSettingsFile NewGrants(params string[] commands) =>
        new(AdapterSettingsRoot.Workspace, Declared.Path,
            Encoding.UTF8.GetBytes(
                "{\"permissions\":{\"allow\":["
                + string.Join(",", System.Array.ConvertAll(commands, c => "\"" + c + "\""))
                + "]}}"));

    // ---- gate 1: IN ---------------------------------------------------------------------------

    [Fact]
    public async Task ATrustedSpawn_CarriesTheRepositorysApprovedCommands_IntoTheJail()
    {
        using var rig = SettingsRig.Create();
        var spawns = rig.Spawns;

        await spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            cliSettings: new[] { NewGrant("Bash(npm test:*)") });

        var delivered = Assert.Single(rig.Engine.LastSpawn!.CliSettingsFiles!);
        Assert.Equal(AdapterSettingsRoot.Workspace, delivered.Root);
        Assert.Equal(Declared.Path, delivered.RelativePath);
        Assert.Contains("npm test", Encoding.UTF8.GetString(delivered.Content));
    }

    [Fact]
    public async Task AnUntrustedSpawn_InheritsNoGrants_EvenWhenTheRepositorysCacheIsWarm()
    {
        using var rig = SettingsRig.Create();
        var spawns = rig.Spawns;

        // A normal session first: this is what warms the daemon's per-(repo, kind) fallback cache, and
        // it is the state an external pull request actually arrives into — a repository the user has
        // been working in and approving commands in all day.
        await spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            cliSettings: new[] { NewGrant("Bash(rm -rf:*)") });
        Assert.NotNull(rig.Engine.LastSpawn!.CliSettingsFiles);

        // Now the untrusted head, exactly as ExternalPrWorkerHost spawns it: no settings passed, so the
        // ONLY thing that could hand it the allowlist is the cache — which the trust gate must refuse.
        await spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Managed, CancellationToken.None,
            agentId: "pr-7",
            queueOrigin: MergeEntryOrigin.External,
            withoutHostCredentials: true);

        Assert.True(
            rig.Engine.LastSpawn!.CliSettingsFiles is null or { Count: 0 },
            "an external pull request's jail must start with NO inherited permission grants — it got "
            + $"{rig.Engine.LastSpawn!.CliSettingsFiles?.Count} of them, which is pre-approved execution "
            + "on code an outside author chose.");
    }

    // ---- gate 2: OUT --------------------------------------------------------------------------

    [Fact]
    public async Task StoppingAHumanAttendedJail_PersistsTheApprovalsMadeInIt()
    {
        using var rig = SettingsRig.Create();
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        var harvested = Assert.Single(result.CliSettings);
        Assert.Equal(Declared.Path, harvested.RelativePath);
        Assert.Equal(SettingsRig.InJailSettings, Encoding.UTF8.GetString(harvested.Content));
        Assert.Equal(RepoHandle, result.RepoHandle);
    }

    [Fact]
    public async Task StoppingAnUnattendedWorker_PersistsNothing_EvenThoughTheFileIsRightThere()
    {
        using var rig = SettingsRig.Create();
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Managed, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        // The jail HAS the file — the same fake engine serves it to the attended test above — so an
        // empty result here can only be the attendance gate, never "there was nothing to find".
        Assert.Empty(result.CliSettings);
    }

    [Fact]
    public void TheAttendancePolicy_AdmitsManualAndCoordinatorSessions_AndRefusesManagedOnes()
    {
        Assert.True(CliHarvestPolicy.MayHarvest(string.Empty));
        Assert.True(CliHarvestPolicy.MayHarvest(AgentRoles.Coordinator));
        Assert.False(CliHarvestPolicy.MayHarvest(AgentRoles.Managed));
    }

    // ---- gate 2b: OUT, the CREDENTIAL leg (F2) --------------------------------------------------

    /// <summary>
    /// The attended baseline, so the refusal below cannot be "the rig serves nothing". The same fake
    /// jail, the same declared path, a role a human drives — the login comes back.
    /// </summary>
    [Fact]
    public async Task StoppingAnAttendedSession_HarvestsTheLogin()
    {
        using var rig = SettingsRig.Create(inJailCredentials: OneLogin(InJailLogin));
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Coordinator, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        var file = Assert.Single(result.CliCredentials, f => f.HomeRelativePath == DeclaredCredentialPath);
        Assert.Equal(InJailLogin, Encoding.UTF8.GetString(file.Content));
    }

    /// <summary>
    /// <b>The F2 defect, stated as a test.</b> A coordinator's workers are spawned Managed, their
    /// terminals are daemon-locked read-only, and the credential harvest had no trust gate at all — so
    /// the daemon read login files back out of a jail no human could have logged into and handed them
    /// to the client to file in the host's durable credential store. The settings beside them were
    /// already refused for exactly this reason.
    /// </summary>
    [Fact]
    public async Task StoppingAnUnattendedWorker_HarvestsNoLogin_EvenThoughTheFileIsRightThere()
    {
        using var rig = SettingsRig.Create(inJailCredentials: OneLogin(InJailLogin));
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Managed, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        // The attended test above proves the file is served, so an empty result here is the gate.
        Assert.Empty(result.CliCredentials);
    }

    /// <summary>The live-harvest entry point takes the same gate. It is the one the client's periodic
    /// sweep drives across EVERY agent on the daemon, so a gate on the stop path alone would leave the
    /// unattended worker's login being collected every few seconds while it ran.</summary>
    [Fact]
    public async Task HarvestingALiveUnattendedWorker_ReturnsNothing()
    {
        using var rig = SettingsRig.Create(inJailCredentials: OneLogin(InJailLogin));
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Managed, CancellationToken.None);

        var result = await rig.Spawns.HarvestCredentialsAsync(agentId, CancellationToken.None);

        Assert.Empty(result.CliCredentials);
        Assert.Empty(result.CliSettings);
    }

    /// <summary>
    /// Refused, not truncated, and refused IN THE SHELL. Half a credential file is a corrupt one, and
    /// it would replace the good copy in the vault, because a harvested path always wins over its
    /// stored copy — so the oversized file must produce nothing at all rather than a prefix.
    /// </summary>
    [Fact]
    public async Task AnOversizedCredentialFile_IsRefused_NotTruncated()
    {
        var oversized = new string('x', AdapterCredentialPolicy.MaxFileBytes + 1);
        using var rig = SettingsRig.Create(inJailCredentials: OneLogin(oversized));
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Coordinator, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        Assert.DoesNotContain(result.CliCredentials, f => f.HomeRelativePath == DeclaredCredentialPath);
    }

    /// <summary>
    /// A settings file declared under <c>credentialPaths</c> (gemini-cli / qwen-code) is still a
    /// settings file: it is held to the SETTINGS ceiling where it sits, not to the roomier credential
    /// one. Sized between the two, so only the right cap can explain the refusal.
    /// </summary>
    [Fact]
    public async Task ASettingsShapedCredentialPath_IsHeldToTheSettingsCeiling()
    {
        Assert.True(AdapterSettingsPolicy.MaxFileBytes < AdapterCredentialPolicy.MaxFileBytes);
        var between = new string('x', AdapterSettingsPolicy.MaxFileBytes + 1);
        using var rig = SettingsRig.Create(inJailCredentials: new[]
        {
            (DeclaredSettingsShapedCredentialPath, Encoding.UTF8.GetBytes(between)),
        });
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Coordinator, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        Assert.Empty(result.CliCredentials);
    }

    /// <summary>
    /// D5b reaches the files that were exempt from it. A settings file parked in <c>credentialPaths</c>
    /// could carry a rule naming Mainguard's own IPC mount straight through both the scrub and the cap;
    /// now it is scrubbed where it sits, without moving it between manifest fields (that move is the
    /// keychain-to-plaintext migration the manifest comment declined).
    /// </summary>
    [Fact]
    public async Task ASettingsShapedCredentialPath_IsScrubbedOfRoleScopedGrants()
    {
        var poisoned = "{\"permissions\":{\"allow\":[\"" + CoordinatorShimGrant + "\",\"Bash(git status:*)\"]}}";
        using var rig = SettingsRig.Create(inJailCredentials: new[]
        {
            (DeclaredSettingsShapedCredentialPath, Encoding.UTF8.GetBytes(poisoned)),
        });
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Coordinator, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        var file = Assert.Single(result.CliCredentials);
        var carried = Encoding.UTF8.GetString(file.Content);
        Assert.DoesNotContain(AgentIpcPaths.SandboxMount, carried, StringComparison.Ordinal);
        Assert.Contains("Bash(git status:*)", carried, StringComparison.Ordinal);
    }

    private static IReadOnlyList<(string Relative, byte[] Content)> OneLogin(string content) =>
        new[] { (DeclaredCredentialPath, Encoding.UTF8.GetBytes(content)) };

    // ---- gate 3: ROLE — one role's tool grant never reaches another role's jail (D5b) ----------

    /// <summary>
    /// <b>The defect.</b> The per-repo store on the reporting machine held
    /// <c>Bash(/opt/mainguard/ipc/mainguard-agent *)</c> — the COORDINATOR's shim — harvested from an
    /// attended coordinator terminal where the owner answered "yes, don't ask again". That store seeds
    /// every later jail in the repository, so a WORKER was being handed a standing grant for the
    /// coordinator's tool. It also meant the live coordinator only worked because of a stale file rather
    /// than because of its own per-role launch grant.
    ///
    /// <para>Restoring is scrubbed as well as harvesting, and that is the half that matters for an install
    /// that already has one: the poisoned entry is on disk today, and scrubbing the way IN neutralises it
    /// with no migration. The owner's own approval rides through untouched — carrying those is the whole
    /// point of the store.</para>
    /// </summary>
    [Fact]
    public async Task AStoredJailGrantForTheDaemonsOwnMount_NeverReachesAJail()
    {
        using var rig = SettingsRig.Create();

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            cliSettings: new[] { NewGrants("Bash(npm test:*)", CoordinatorShimGrant) });

        var delivered = Assert.Single(rig.Engine.LastSpawn!.CliSettingsFiles!);
        var text = Encoding.UTF8.GetString(delivered.Content);
        Assert.DoesNotContain(AgentIpcPaths.SandboxMount, text, StringComparison.Ordinal);
        Assert.Contains("npm test", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the store stops acquiring one: an attended stop harvests the file with the mount's grant
    /// removed, so the very next stop repairs an already-poisoned store rather than rewriting it.
    /// </summary>
    [Fact]
    public async Task StoppingAnAttendedJail_HarvestsTheApprovalsWithoutTheJailsOwnToolGrant()
    {
        const string WithGrant =
            "{\"permissions\":{\"allow\":[\"Bash(git status:*)\",\"" + CoordinatorShimGrant + "\"]}}";
        using var rig = SettingsRig.Create(WithGrant);
        var agentId = await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: AgentRoles.Coordinator, CancellationToken.None);

        var result = await rig.Spawns.StopAsync(agentId, CancellationToken.None);

        var harvested = Assert.Single(result.CliSettings);
        var text = Encoding.UTF8.GetString(harvested.Content);
        Assert.DoesNotContain(AgentIpcPaths.SandboxMount, text, StringComparison.Ordinal);
        // The negative control: the harvest still WORKS. Without this, a scrub that dropped the whole
        // file — or a harvest that had quietly stopped running — would pass the assertion above.
        Assert.Contains("git status", text, StringComparison.Ordinal);
    }

    // ---- the filter the client's own paths pass through ----------------------------------------

    [Fact]
    public void OnlyPathsTheAdapterDeclares_ReachTheJail()
    {
        var marker = new InstalledAdapterMarker(
            AgentKind, "1.0.0", new[] { "/bin/true" }, SettingsPaths: new[] { Declared });

        var kept = SandboxAgentLauncher.FilterCliSettings(
            new[]
            {
                NewGrant("Bash(ls:*)"),
                // Right path, WRONG root — the pair is the identity, so this must not slip through and
                // land a permission allowlist in the jail's home instead of its checkout.
                new SandboxSettingsFile(AdapterSettingsRoot.Home, Declared.Path, new byte[] { 1 }),
                // A path the adapter never declared: the client names paths on the wire, so an
                // unfiltered one is a compromised client planting pre-approved commands anywhere.
                new SandboxSettingsFile(AdapterSettingsRoot.Workspace, ".ssh/authorized_keys", new byte[] { 1 }),
            },
            marker);

        var single = Assert.Single(kept!);
        Assert.Equal(Declared.Path, single.RelativePath);
        Assert.Equal(AdapterSettingsRoot.Workspace, single.Root);
    }

    [Fact]
    public void AnOversizedSettingsFile_IsRefused_RatherThanCarriedIntoEveryFutureJail()
    {
        var marker = new InstalledAdapterMarker(
            AgentKind, "1.0.0", new[] { "/bin/true" }, SettingsPaths: new[] { Declared });

        var kept = SandboxAgentLauncher.FilterCliSettings(
            new[]
            {
                new SandboxSettingsFile(
                    AdapterSettingsRoot.Workspace, Declared.Path,
                    new byte[AdapterSettingsPolicy.MaxFileBytes + 1]),
            },
            marker);

        Assert.Null(kept);
    }

    // ---- keeping the allowlist out of the user's git history ------------------------------------

    [Fact]
    public async Task AJailIsToldToIgnoreTheWorkspaceSettingsPath_EvenWhenThereIsNothingToRestore()
    {
        using var rig = SettingsRig.Create();

        // The FIRST-ever session in a repository: no stored approvals, so nothing is restored. The CLI
        // will create `.probe/settings.local.json` in /workspace itself the moment the user approves
        // something — and /workspace is the tree the agent commits with `git add -A`. If the ignore
        // list were derived from the restore payload, this session — the one that creates the file —
        // would be the only one unprotected, and the user's permission allowlist would land in their
        // own repository.
        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None);

        Assert.Null(rig.Engine.LastSpawn!.CliSettingsFiles);
        Assert.Equal(new[] { Declared.Path }, rig.Engine.LastSpawn!.WorkspaceIgnorePaths);
    }

    /// <summary>
    /// The settings path is not the only thing Mainguard writes into the tree the agent commits: the
    /// launcher also stages the adapter's declared instructions file at the worktree root. Driven through
    /// the REAL spawn, and asserting both halves of the same spawn, because the failure this closes is
    /// exactly a disagreement between them — the file was written and the ignore list did not name it.
    ///
    /// <para>A test on <c>DeclaredWorkspaceIgnorePaths</c> alone would stay green while the spawn kept
    /// sending the old list: phase 3's own M7 shape, a correct function nobody calls correctly.</para>
    /// </summary>
    [Fact]
    public async Task TheInstructionsFileTheLauncherStages_IsAlsoWhatTheJailIsToldToIgnore()
    {
        using var rig = SettingsRig.Create(instructionsFile: "PROBE_INSTRUCTIONS.md");

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None);

        Assert.Equal(
            new[] { Declared.Path, "PROBE_INSTRUCTIONS.md" }, rig.Engine.LastSpawn!.WorkspaceIgnorePaths);

        // …and it really was written, at the root of the worktree that spawn created.
        var staged = Path.Combine(rig.Engine.LastSpawn!.WorktreePath, "PROBE_INSTRUCTIONS.md");
        Assert.True(File.Exists(staged), $"the launcher staged nothing at {staged}");
    }

    /// <summary>
    /// The one case an exclude cannot cover, driven through the real spawn: the repository already has a
    /// file of that name, so it is tracked, so <c>info/exclude</c> is inert for it and a write is a
    /// modification <c>git add -A</c> stages. Mainguard replacing the user's own project instructions is
    /// a worse outcome than the stray untracked file this change is about.
    /// </summary>
    [Fact]
    public async Task AFileTheRepositoryAlreadyHas_IsNotReplacedByMainguardsBriefing()
    {
        var theirs = "# the user's own instructions\n";
        // Every worktree this rig creates arrives already carrying that file — the way a real checkout of
        // a repository that tracks one does.
        using var rig = SettingsRig.Create(
            instructionsFile: "PROBE_INSTRUCTIONS.md",
            seedWorktreeFile: ("PROBE_INSTRUCTIONS.md", theirs));

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None);

        var path = Path.Combine(rig.Engine.LastSpawn!.WorktreePath, "PROBE_INSTRUCTIONS.md");
        Assert.Equal(theirs, File.ReadAllText(path));
    }

    [Fact]
    public void OnlyWorkspaceRootedDeclarations_BecomeIgnoreEntries()
    {
        var marker = new InstalledAdapterMarker(
            AgentKind, "1.0.0", new[] { "/bin/true" },
            SettingsPaths: new[]
            {
                Declared,
                // $HOME is a tmpfs outside any repository — ignoring it would be meaningless noise in
                // the exclude file, and a sign the root was not being read.
                new AdapterSettingsPath("home", ".probe/settings.json"),
                // A malformed declaration must not reach a path that is written into a git config file.
                new AdapterSettingsPath("workspace", "../escape.json"),
            });

        Assert.Equal(
            new[] { Declared.Path }, SandboxAgentLauncher.DeclaredWorkspaceSettingsPaths(marker));
    }

    [Fact]
    public void AnAdapterThatDeclaresNoSettings_RestoresNothing()
    {
        var marker = new InstalledAdapterMarker(AgentKind, "1.0.0", new[] { "/bin/true" });

        Assert.Null(SandboxAgentLauncher.FilterCliSettings(new[] { NewGrant("Bash(ls:*)") }, marker));
    }

    /// <summary>
    /// An in-proc daemon over a fake substrate, wired the production way: the adapter marker declares
    /// <see cref="Declared"/>, and the spawn service comes out of the daemon's own container rather
    /// than being hand-assembled — so these tests cannot pass against a copy of the wiring that the app
    /// does not use.
    ///
    /// <para>Its sandbox engine does two things: it RECORDS every spawn request (so what actually
    /// reaches a jail is observable), and it answers the daemon's settings-harvest exec with a file, so
    /// "nothing was harvested" is always a decision and never an absence.</para>
    /// </summary>
    private sealed class SettingsRig : IDisposable
    {
        /// <summary>What the fake jail holds at the declared settings path.</summary>
        public const string InJailSettings = "{\"permissions\":{\"allow\":[\"Bash(git status:*)\"]}}";

        private readonly string _root;

        private SettingsRig(string root) => _root = root;

        public required WebApplicationFactory<Program> Host { get; init; }

        public required RecordingSandboxEngine Engine { get; init; }

        public AgentSpawnService Spawns => Host.Services.GetRequiredService<AgentSpawnService>();

        /// <param name="inJailSettings">What the fake jail's declared settings file holds. Defaults to the
        /// ordinary allowlist; a test that cares about the role-scoped-grant boundary (D5b) puts a jail's
        /// own IPC grant in it, which is exactly what the reporting machine's store was found holding.</param>
        /// <param name="instructionsFile">The adapter's declared instructions file, if any — the second
        /// thing Mainguard writes into <c>/workspace</c>. Null keeps the pre-existing shape (an adapter
        /// with no file-side delivery), which is what every other test here wants.</param>
        /// <param name="seedWorktreeFile">A file every created worktree already carries, so a test can
        /// stand where the user's own repository tracks the name the adapter declares.</param>
        /// <param name="inJailCredentials">What the fake jail holds at the adapter's declared
        /// <c>credentialPaths</c> — the F2 leg. Null leaves the jail with no login files at all, which
        /// is what every settings test wants.</param>
        public static SettingsRig Create(
            string? inJailSettings = null,
            string? instructionsFile = null,
            (string Path, string Content)? seedWorktreeFile = null,
            IReadOnlyList<(string Relative, byte[] Content)>? inJailCredentials = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "mg-settings-gate-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "repos", RepoHandle)); // "provisioned"

            var registry = Path.Combine(root, "registry");
            Directory.CreateDirectory(registry);
            File.WriteAllText(
                Path.Combine(registry, AgentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    AgentKind, "1.0.0", new[] { "/bin/true" }, SettingsPaths: new[] { Declared },
                    InstructionsFile: instructionsFile,
                    CredentialPaths: DeclaredCredentialPaths)));

            var engine = new RecordingSandboxEngine(inJailSettings ?? InJailSettings, inJailCredentials);
            var host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(new FakeEnvironment(root, engine, seedWorktreeFile));
                services.AddSingleton(new InstalledAdapterCatalog(registry));
                services.AddSingleton(sp => new AgentCliBinder(
                    sp.GetRequiredService<TerminalSessionManager>(),
                    sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.SessionLeader>(),
                    sp.GetRequiredService<AgentSessionStore>(),
                    sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(),
                    _ => new InertTerminalSession()));
            }));

            return new SettingsRig(root) { Host = host, Engine = engine };
        }

        public void Dispose()
        {
            Host.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* never fail a test from cleanup */ }
        }

        /// <summary>Records what each spawn was given, and serves the declared settings file back on a
        /// harvest exec. Every other member is inert.</summary>
        public sealed class RecordingSandboxEngine : ISandboxEngine
        {
            private readonly ConcurrentQueue<SandboxSpawnRequest> _spawns = new();
            private readonly string _inJailSettings;
            private readonly IReadOnlyList<(string Relative, byte[] Content)> _inJailCredentials;

            public RecordingSandboxEngine(
                string? inJailSettings = null,
                IReadOnlyList<(string Relative, byte[] Content)>? inJailCredentials = null)
            {
                _inJailSettings = inJailSettings ?? InJailSettings;
                _inJailCredentials = inJailCredentials ?? Array.Empty<(string, byte[])>();
            }

            /// <summary>The most recent spawn request — what the jail would really have received.</summary>
            public SandboxSpawnRequest? LastSpawn => _spawns.LastOrDefault();

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            {
                _spawns.Enqueue(request);
                return Task.FromResult(new SandboxHandle($"ctr-{request.AgentId}", Reused: false));
            }

            public Task<SandboxExecResult> ExecAsync(
                string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            {
                // The harvest exec is `sh -c <script> sh <path> <maxBytes>`; the path is what identifies
                // which declared file is being read. Anything else answers "absent", exactly as a real
                // jail would for a path the CLI never wrote.
                var settingsPath = ContainerSpecBuilder.WorkspaceTarget + "/" + Declared.Path;
                if (command.Contains(settingsPath))
                {
                    return Task.FromResult(Serve(command, Encoding.UTF8.GetBytes(_inJailSettings)));
                }

                // The credential leg reads the same way out of the tmpfs $HOME.
                foreach (var (relative, bytes) in _inJailCredentials)
                {
                    if (command.Contains(ContainerSpecBuilder.AgentHome + "/" + relative))
                    {
                        return Task.FromResult(Serve(command, bytes));
                    }
                }

                return Task.FromResult(new SandboxExecResult(1, string.Empty, string.Empty));
            }

            /// <summary>
            /// What the real in-shell script does, so a size refusal is exercised rather than asserted
            /// about. The daemon's whole cap argument is that the check happens in the CONTAINER — a
            /// fake that answered 0 with the bytes and let the daemon measure them afterwards would be
            /// testing a check the production path does not have.
            /// </summary>
            private static SandboxExecResult Serve(IReadOnlyList<string> command, byte[] content)
            {
                // `sh -c <script> sh <path> <maxBytes>` — the ceiling is the last argument.
                if (int.TryParse(
                        command[^1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var max)
                    && content.Length > max)
                {
                    return new SandboxExecResult(2, string.Empty, string.Empty);
                }

                return new SandboxExecResult(0, Convert.ToBase64String(content), string.Empty);
            }

            public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class FakeEnvironment : IAgentEnvironment
        {
            private readonly string _root;

            public FakeEnvironment(
                string root, ISandboxEngine sandboxes, (string Path, string Content)? seedWorktreeFile = null)
            {
                _root = root;
                Sandboxes = sandboxes;
                Repos = new FakeProvisioner(root);
                Worktrees = new FakeWorktrees(root, seedWorktreeFile);
            }

            public string SubstrateId => "fake";

            public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

            public ISandboxEngine Sandboxes { get; }

            public IRepoProvisioner Repos { get; }

            public IAgentWorktreeManager Worktrees { get; }

            public IEgressPolicy Egress { get; } = new FakeEgress();

            public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

            private sealed class FakeProvisioner : IRepoProvisioner
            {
                private readonly string _root;

                public FakeProvisioner(string root) => _root = root;

                public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                    throw new NotSupportedException("not exercised");

                public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
            }

            private sealed class FakeWorktrees : IAgentWorktreeManager
            {
                private readonly string _root;
                private readonly (string Path, string Content)? _seed;

                public FakeWorktrees(string root, (string Path, string Content)? seed = null)
                {
                    _root = root;
                    _seed = seed;
                }

                public string CreateAgentWorktree(string repoHash, string agentId)
                {
                    var path = Path.Combine(_root, "wt", repoHash, agentId);
                    Directory.CreateDirectory(path);
                    if (_seed is { } seed)
                    {
                        // A checkout that already carries this file — i.e. the repository tracks it.
                        File.WriteAllText(Path.Combine(path, seed.Path), seed.Content);
                    }

                    return path;
                }

                public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
                {
                    try { Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true); }
                    catch (DirectoryNotFoundException) { }
                }

                public void Prune(string repoHash) { }

                public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                    Array.Empty<Mainguard.Git.Models.WorktreeItem>();
            }

            private sealed class FakeEgress : IEgressPolicy
            {
                public EgressAllowlist Allowlist { get; } =
                    EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

                public string NetworkName => "fake-net";

                public string ProxyUrl => "http://fake-proxy:3128";

                public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

                public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
            }
        }

        private sealed class InertTerminalSession : ITerminalSession
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Stream IO { get; } = new MemoryStream();

            public Task<int> ExitCode => _exit.Task;

            public void Resize(int cols, int rows) { }

            public void Kill() => _exit.TrySetResult(0);

            public void Dispose() => Kill();
        }
    }
}
