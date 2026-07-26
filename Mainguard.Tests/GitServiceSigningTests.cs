using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

// TI-15 (integration): commit & tag signing end-to-end. These need a real gpg — the SAME binary
// git will invoke — so they are gated [RequiresGpgFact] and report a genuine Skipped (not a
// failure) when gpg is absent.
//
// The signing environment is fully ephemeral and never touches the developer's real keyring:
//   * a throwaway GNUPGHOME temp dir (set as a process env var for this test's lifetime so git's
//     child gpg reads it, restored on Dispose),
//   * a passphrase-less ed25519 key generated in batch/loopback mode with that same gpg binary,
//   * the repo's LOCAL git config points user.signingkey/gpg.program at that key/binary.
//
// The collection disables parallelization so no other test's git runs while GNUPGHOME is swapped.
[Trait("Category", "RequiresGpg")]
[Collection("Signing")]
public sealed class GitServiceSigningTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GpgTestEnvironment _gpg = new();
    private readonly UserPreferences _prefs = new();
    private readonly GitService _service;

    public GitServiceSigningTests()
    {
        // The GitService reads signing config from these prefs on each commit/tag.
        _service = new GitService(() => _prefs);
        if (_gpg.Available)
        {
            _prefs.SignCommits = true;
            _prefs.GpgFormat = "openpgp";
            _prefs.SigningKey = _gpg.Fingerprint;
            _prefs.GpgProgram = _gpg.GpgProgram;
        }
    }

    public void Dispose()
    {
        _gpg.Dispose();
        _fx.Dispose();
    }

    [RequiresGpgFact]
    public void Commit_WithSigningOn_ShouldProduceVerifiableSignature()
    {
        // Seed one commit through the ordinary (unsigned) LibGit2Sharp path, then a signed one.
        _fx.WriteFile("a.txt", "hello\n");
        StageAll();
        _service.Commit(_fx.RepoPath, "signed commit");

        // git itself must verify the signature (exit 0)…
        var (verifyCode, _, verifyErr) = RunGit("verify-commit", "HEAD");
        Assert.True(verifyCode == 0, $"verify-commit failed: {verifyErr}");

        // …and our %G? read must classify it as a good signature.
        var head = HeadSha();
        var statuses = _service.GetSignatureStatuses(_fx.RepoPath, new[] { head });
        Assert.Equal(SignatureStatus.Good, statuses[head].Status);
        Assert.True(statuses[head].IsVerified);
        Assert.False(string.IsNullOrWhiteSpace(statuses[head].Signer));
    }

    [RequiresGpgFact]
    public void Commit_WithSigningOff_ShouldShowStatusNone()
    {
        _prefs.SignCommits = false; // unsigned path (LibGit2Sharp)

        _fx.WriteFile("a.txt", "hello\n");
        StageAll();
        _service.Commit(_fx.RepoPath, "unsigned commit");

        var head = HeadSha();
        var statuses = _service.GetSignatureStatuses(_fx.RepoPath, new[] { head });
        Assert.Equal(SignatureStatus.None, statuses[head].Status);
        Assert.False(statuses[head].IsSigned);

        // And git agrees there's nothing to verify.
        var (verifyCode, _, _) = RunGit("verify-commit", head);
        Assert.NotEqual(0, verifyCode);
    }

    [RequiresGpgFact]
    public void Tag_WithSigningOn_ShouldProduceVerifiableSignedTag()
    {
        _fx.WriteFile("a.txt", "hello\n");
        StageAll();
        _service.Commit(_fx.RepoPath, "base");
        var head = HeadSha();

        // Annotated tag with signing on → `git tag -s`.
        _service.CreateTag(_fx.RepoPath, "v1.0-signed", head, "release one");

        var (verifyCode, _, verifyErr) = RunGit("verify-tag", "v1.0-signed");
        Assert.True(verifyCode == 0, $"verify-tag failed: {verifyErr}");
    }

    [RequiresGpgFact]
    public void Commit_SignedThenReadWithoutKey_ShouldNotVerifyGood()
    {
        _fx.WriteFile("a.txt", "hello\n");
        StageAll();
        _service.Commit(_fx.RepoPath, "signed commit");
        var head = HeadSha();

        // Re-read %G? from a fresh, empty GNUPGHOME that lacks the public key: git can no longer
        // verify → the status must fall out of "Good" (typically CannotCheck). This is the
        // "signed-but-unknown-key" Master Doc edge case, produced deterministically.
        using var emptyHome = new GnupgHomeScope();
        var statuses = _service.GetSignatureStatuses(_fx.RepoPath, new[] { head });
        Assert.NotEqual(SignatureStatus.Good, statuses[head].Status);
        Assert.NotEqual(SignatureStatus.None, statuses[head].Status); // still carries a signature
    }

    [RequiresGpgFact]
    public async System.Threading.Tasks.Task SigningFailure_WithBogusKey_ShouldThrowTyped_NotHang()
    {
        _prefs.SigningKey = "0xDEADBEEFDEADBEEF"; // no such secret key

        _fx.WriteFile("a.txt", "hello\n");
        StageAll();

        // Must fail fast with a typed error (GIT_TERMINAL_PROMPT=0 keeps it from hanging on
        // pinentry). The timeout race guards against a regression that re-introduces a hang.
        var commit = System.Threading.Tasks.Task.Run(() => _service.Commit(_fx.RepoPath, "should fail"));
        var finished = await System.Threading.Tasks.Task.WhenAny(
            commit, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == commit, "signing failure hung instead of throwing.");
        await Assert.ThrowsAsync<GitOperationException>(() => commit);
    }

    // --- helpers -------------------------------------------------------------

    private void StageAll()
    {
        using var repo = new LibGit2Sharp.Repository(_fx.RepoPath);
        LibGit2Sharp.Commands.Stage(repo, "*");
    }

    private string HeadSha()
    {
        using var repo = new LibGit2Sharp.Repository(_fx.RepoPath);
        return repo.Head.Tip!.Sha;
    }

    private (int Code, string Out, string Err) RunGit(params string[] args)
        => GpgTestEnvironment.Run("git", _fx.RepoPath, args);
}

/// <summary>
/// Temporarily points the process <c>GNUPGHOME</c> env var at a fresh empty temp dir and restores
/// the prior value on dispose. Used to re-read a signature with no key material present.
/// </summary>
internal sealed class GnupgHomeScope : IDisposable
{
    private readonly string? _previous;
    private readonly string _home;

    public GnupgHomeScope()
    {
        _previous = Environment.GetEnvironmentVariable("GNUPGHOME");
        _home = Path.Combine(Path.GetTempPath(), "mainguard-gpg-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("GNUPGHOME", GpgTestEnvironment.ToGpgHome(_home));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GNUPGHOME", _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Ephemeral gpg signing environment for the TI-15 tests. Locates the gpg binary git will invoke,
/// stands up a throwaway GNUPGHOME, and generates a passphrase-less ed25519 signing key there.
/// <see cref="Available"/> is false (and the tests skip) when no usable gpg is found or key
/// generation fails — nothing ever touches the developer's real keyring.
/// </summary>
internal sealed class GpgTestEnvironment : IDisposable
{
    private readonly string? _previousGnupgHome;
    private readonly string _home;

    public bool Available { get; }
    public string GpgProgram { get; } = "gpg";
    public string Fingerprint { get; } = string.Empty;

    private const string Uid = "Mainguard Test <mainguard-signing-test@example.invalid>";

    public GpgTestEnvironment()
    {
        _home = Path.Combine(Path.GetTempPath(), "mainguard-gpg-" + Guid.NewGuid().ToString("N"));

        var gpg = LocateGpg();
        if (gpg == null) { Available = false; return; }
        GpgProgram = gpg;

        Directory.CreateDirectory(_home);
        _previousGnupgHome = Environment.GetEnvironmentVariable("GNUPGHOME");
        // Git-for-Windows' gpg is an MSYS build: a Windows path like C:\...\home makes gpg-agent
        // derive a socket name containing the drive-letter colon, which libassuan rejects
        // ("':' are not allowed in the socket name"). Handing it the MSYS /c/... form (colon-free)
        // is exactly what gpg uses for its own default homedir, so the agent starts cleanly.
        Environment.SetEnvironmentVariable("GNUPGHOME", ToGpgHome(_home));

        try
        {
            // Passphrase-less key, batch + loopback so there's no pinentry. The empty passphrase is
            // not a secret, so passing "" as an argv element does not violate G-4.
            var gen = Run(GpgProgram, _home,
                "--batch", "--yes", "--no-tty", "--pinentry-mode", "loopback", "--passphrase", "",
                "--quick-generate-key", Uid, "ed25519", "sign", "never");
            if (gen.Code != 0) { Available = false; return; }

            Fingerprint = ReadFingerprint();
            Available = !string.IsNullOrEmpty(Fingerprint);
        }
        catch
        {
            Available = false;
        }
    }

    private string ReadFingerprint()
    {
        var (code, output, _) = Run(GpgProgram, _home, "--list-secret-keys", "--with-colons");
        if (code != 0) return string.Empty;
        // The `fpr` record's field 10 (index 9) is the full fingerprint.
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split(':');
            if (fields[0] == "fpr" && fields.Length > 9 && fields[9].Length > 0)
                return fields[9];
        }
        return string.Empty;
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            // Stop the gpg-agent spawned in our home so its socket/files unlock before we delete.
            try
            {
                var conf = LocateSibling(GpgProgram, "gpgconf");
                if (conf != null) Run(conf, _home, "--kill", "all");
            }
            catch { }
        }
        Environment.SetEnvironmentVariable("GNUPGHOME", _previousGnupgHome);
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Converts a Windows absolute path (<c>C:\a\b</c>) to the MSYS form (<c>/c/a/b</c>) that the
    /// Git-for-Windows gpg expects for GNUPGHOME; non-Windows paths pass through unchanged.
    /// </summary>
    internal static string ToGpgHome(string path)
    {
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return "/" + char.ToLowerInvariant(path[0]) + path.Substring(2).Replace('\\', '/');
        return path;
    }

    // --- gpg discovery -------------------------------------------------------

    private static string? LocateGpg()
    {
        // 1) gpg already on PATH.
        if (Probe("gpg")) return "gpg";

        // 2) The gpg bundled with Git for Windows (git's own gpg), next to the git binary.
        foreach (var gitPath in LocateGitBinaries())
        {
            var gitDir = Path.GetDirectoryName(gitPath);
            if (gitDir == null) continue;
            var root = Directory.GetParent(gitDir)?.FullName;
            var candidates = new List<string>();
            if (root != null)
            {
                candidates.Add(Path.Combine(root, "usr", "bin", "gpg.exe"));
                candidates.Add(Path.Combine(root, "mingw64", "bin", "gpg.exe"));
            }
            candidates.Add(Path.Combine(gitDir, "gpg.exe"));
            foreach (var c in candidates)
                if (File.Exists(c) && Probe(c)) return c;
        }
        return null;
    }

    private static IEnumerable<string> LocateGitBinaries()
    {
        foreach (var locator in new[] { "where", "which" })
        {
            (int Code, string Out, string Err) res;
            try { res = Run(locator, null, "git"); }
            catch { continue; }
            if (res.Code != 0) continue;
            foreach (var line in res.Out.Split('\n'))
            {
                var p = line.Trim();
                if (p.Length > 0 && File.Exists(p)) yield return p;
            }
        }
    }

    private static string? LocateSibling(string program, string sibling)
    {
        if (program == "gpg") return sibling; // rely on PATH when gpg itself came from PATH
        var dir = Path.GetDirectoryName(program);
        if (dir == null) return null;
        var candidate = Path.Combine(dir, sibling + ".exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool Probe(string gpg)
    {
        try { return Run(gpg, null, "--version").Code == 0; }
        catch { return false; }
    }

    // --- process runner ------------------------------------------------------

    public static (int Code, string Out, string Err) Run(string fileName, string? workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        // Never hang on a credential/terminal prompt in a headless test.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"'{fileName}' did not exit in time.");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

[CollectionDefinition("Signing", DisableParallelization = true)]
public sealed class SigningCollection { }
