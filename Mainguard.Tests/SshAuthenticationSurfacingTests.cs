using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LibGit2Sharp;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;
using Repository = Mainguard.Git.Models.Repository;

namespace Mainguard.Tests;

/// <summary>
/// SSH authentication was dead at four layers at once: <see cref="SshAuthenticationException"/> was
/// declared and caught but <b>never thrown by anything</b>; its handler set a visibility flag and showed
/// no notification; that flag and its two commands were bound in no <c>.axaml</c>; and the secret the
/// handler wrote (<c>"ssh_passphrase"</c>) was never read, because <c>SshKeyService</c> keys passphrases
/// per key as <c>sshpass_&lt;path&gt;</c>.
///
/// <para>The net effect was worse than nothing. git's stderr for a locked or untrusted key is
/// <c>Permission denied (publickey)</c>, which the generic classifier reads as an auth failure — so an
/// SSH key problem was reported as "sign in to &lt;host&gt;" and opened the <b>personal access token</b>
/// dialog, a remedy that cannot fix a key. The prompt that was supposed to be the real answer never
/// appeared, because nothing ever threw the exception that would have shown it.</para>
///
/// <para>The dead prompt was removed rather than completed (finishing it means a modal plus an
/// <c>SSH_ASKPASS</c> bridge that puts the passphrase on disk). What is asserted here is the part that
/// matters: the failure is now classified correctly and REACHES the user with guidance that applies.</para>
/// </summary>
public class SshAuthenticationSurfacingTests
{
    /// <summary>
    /// Points the repo at an SSH-form remote whose <c>ssh</c> always answers the way a locked or
    /// untrusted key does. <c>core.sshCommand</c> is run through the shell by git, so this reproduces the
    /// exact stderr the classifier sees without needing an SSH server.
    /// </summary>
    private static void GiveRepoARefusingSshRemote(TempRepoFixture fx)
    {
        using var repo = new LibGit2Sharp.Repository(fx.RepoPath);
        repo.Network.Remotes.Add("origin", "ssh://git@ssh.invalid/demo/repo.git");
        repo.Config.Set(
            "core.sshCommand",
            "printf 'git@ssh.invalid: Permission denied (publickey).\\n' >&2; exit 255",
            ConfigurationLevel.Local);
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. Before, this threw <see cref="AuthenticationRequiredException"/> —
    /// the type whose UI handler opens the token dialog. Nothing in the repository threw
    /// <see cref="SshAuthenticationException"/> at all (1 definition, 1 catch, 0 throws).
    /// </summary>
    [Fact]
    public void SshRemote_RefusedKey_ThrowsSshAuthentication_NotTokenAuthentication()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo\n", "chore: seed");
        GiveRepoARefusingSshRemote(fx);

        var git = new GitService();
        var ex = Assert.Throws<SshAuthenticationException>(() => git.Fetch(fx.RepoPath));

        // The message has to name a remedy that can actually work on a key.
        Assert.Contains("SSH", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SSH Keys", ex.Message, StringComparison.Ordinal);
        Assert.IsNotType<AuthenticationRequiredException>(ex);
    }

    /// <summary>
    /// An HTTPS remote must keep going to the token path — the new classification is scoped to SSH-form
    /// remotes and must not swallow the case the Accounts dialog genuinely fixes.
    /// </summary>
    [Fact]
    public void HttpsRemote_RefusedAuth_StillRoutesToTheTokenPath()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo\n", "chore: seed");
        using (var repo = new LibGit2Sharp.Repository(fx.RepoPath))
        {
            repo.Network.Remotes.Add("origin", "https://mainguard.invalid/demo/repo.git");
        }

        var git = new GitService();
        var ex = Record.Exception(() => git.Fetch(fx.RepoPath));

        Assert.NotNull(ex);
        Assert.IsNotType<SshAuthenticationException>(ex);
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER, and this is the user-facing half. Before, the handler set
    /// <c>IsSshPassphrasePromptVisible = true</c> — a property no view bound — and showed NO
    /// notification, so a push/pull over a passphrase-protected remote simply appeared to do nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task FailingSshFetch_ShowsAnErrorToast_InsteadOfDoingNothingVisible()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo\n", "chore: seed");
        GiveRepoARefusingSshRemote(fx);

        using var dash = new RepoDashboardViewModel(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
        await Pump();
        dash.Toasts.Clear();

        await dash.FetchCommand.ExecuteAsync(null);
        await Pump();

        var toast = Assert.Single(dash.Toasts);
        Assert.True(toast.IsError, "an SSH auth failure was reported as if it were routine information");

        // The remedy has to be the one that can work. Deliberately NOT a loose "contains SSH" — the old
        // token-path message said "sign in to ssh.invalid", which matches that and is exactly the wrong
        // advice. What must be present is the SSH Keys page; what must be absent is the token pitch.
        Assert.Contains("SSH Keys", toast.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", toast.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sign in", toast.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Pump()
    {
        for (var i = 0; i < 50; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
