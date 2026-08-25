using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Tests.TestTools;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Settings → Toolchains: the human-facing half of the user-managed toolchain channel. The channel
/// being right is not the feature — with no working page a user cannot install a language at all, and
/// the thing that has repeatedly shipped broken in this repo is not the service but the surface over
/// it (a button that renders visible and is permanently disabled; a row that says "Installed" because
/// a file exists).
///
/// <para>These drive the REAL <see cref="ToolchainChannel"/> over a fake
/// <see cref="IAdapterInstallHost"/> — the shipped fetch → sha256-verify → unpack → run-it policy
/// executes unchanged, minus the VM. The bar they hold: the list is a measurement and never a guess (a
/// toolchain present at the WRONG version is NOT installed), a failure names its cause and leaves the
/// row retryable, Remove leaves behind a re-probed list rather than an assumption, and every per-row
/// command re-publishes its enablement when the row it reads changes.</para>
/// </summary>
public class ToolchainSettingsUiTests
{
    [Fact]
    public async Task ShouldListCuratedToolchains_WithInstalledStateFromTheProbe()
    {
        var fx = new Fixture();
        fx.Host.PreInstall("python-3");
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.IsLoading);
        Assert.False(vm.HasLoadError);
        Assert.Equal(new[] { "python-3", "node-22" }, vm.Toolchains.Select(t => t.Id));

        var python = vm.Toolchains.Single(t => t.Id == "python-3");
        Assert.True(python.IsInstalled);
        Assert.True(python.CanRemove);
        Assert.False(python.CanInstall);
        // The pinned version is surfaced, not hidden — the user sees exactly what they'd get.
        Assert.Equal("v3.12.13", python.VersionLabel);
        Assert.Contains("test suite", python.Summary, StringComparison.OrdinalIgnoreCase);

        var node = vm.Toolchains.Single(t => t.Id == "node-22");
        Assert.False(node.IsInstalled);
        Assert.True(node.CanInstall);
        Assert.Equal("Not installed", node.NotInstalledDetail);
    }

    [Fact]
    public async Task Install_ShouldFlipTheRowToInstalled()
    {
        var fx = new Fixture();
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Toolchains.Single(t => t.Id == "node-22");
        await vm.InstallCommand.ExecuteAsync(row);

        Assert.True(row.IsInstalled);
        Assert.False(row.IsFailed);
        Assert.True(row.CanRemove);
        Assert.False(vm.IsBusy);
        // Installed in the VM, and only the one that was asked for.
        Assert.Equal(new[] { "node-22" }, fx.Host.InstalledIds);
        // The marker is written LAST — its presence here means the probe was green first.
        Assert.Contains("/toolchains/registry/node-22.json", fx.Host.WrittenFiles.Single());
    }

    [Fact]
    public async Task InstallFailure_ShouldNameTheCause_AndLeaveTheRowRetryable()
    {
        var fx = new Fixture();
        // The bytes that arrive do not hash to the manifest pin — the corruption/interception case the
        // pin exists to refuse. Checked on the host, so the refusal happens before the VM sees anything.
        fx.Payloads.Corrupt = true;
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Toolchains.Single(t => t.Id == "node-22");
        await vm.InstallCommand.ExecuteAsync(row);

        Assert.True(row.IsFailed);
        Assert.False(row.IsInstalled);
        Assert.Contains("checksum", row.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // Nothing was unpacked — the refusal is upstream of extraction, and the copy says so.
        Assert.Empty(fx.Host.Extracted);
        // …and upstream of the VM entirely: the bytes never crossed the boundary.
        Assert.Empty(fx.Host.Staged);
        Assert.True(row.CanInstall); // retryable in place, not a dead row
        Assert.True(vm.InstallCommand.CanExecute(row));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ToolchainRunningTheWrongVersion_ShouldBeReportedAsNotInstalled()
    {
        // The whole reason the list re-probes instead of reading a marker: a toolchain that RUNS but is
        // not what this Mainguard pinned must not be presented as the pinned one, or a verification jail
        // and the developer's machine quietly disagree about what "the tests passed" means.
        var fx = new Fixture();
        fx.Host.PreInstall("python-3", reportsVersion: "3.11.9");
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Toolchains.Single(t => t.Id == "python-3");
        Assert.False(row.IsInstalled);
        Assert.True(row.CanInstall);
        // And it says which version is there and which was expected — not "not installed" over the top
        // of a working interpreter.
        Assert.Contains("3.12.13", row.NotInstalledDetail);
        Assert.Contains("3.11.9", row.NotInstalledDetail);
    }

    [Fact]
    public async Task Remove_ShouldFlipTheRowBack_AndReProbe()
    {
        var fx = new Fixture();
        fx.Host.PreInstall("python-3");
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());
        await vm.RefreshCommand.ExecuteAsync(null);
        var probesAfterFirstList = fx.Host.Probes;

        await vm.RemoveCommand.ExecuteAsync(vm.Toolchains.Single(t => t.Id == "python-3"));

        // Rows are rebuilt by the post-removal refresh, so read the row back out of the list.
        var row = vm.Toolchains.Single(t => t.Id == "python-3");
        Assert.False(row.IsInstalled);
        Assert.True(row.CanInstall);
        Assert.False(row.IsFailed);
        Assert.Empty(fx.Host.InstalledIds);
        Assert.False(vm.IsBusy);
        // The state shown after a removal is measured, not assumed.
        Assert.True(fx.Host.Probes > probesAfterFirstList,
            "Remove did not re-probe — the list would be showing what the ViewModel believes, not what the VM has.");
    }

    [Fact]
    public async Task RemoveFailure_ShouldNameTheCause_OnTheReProbedRow()
    {
        // A removal that only half-succeeded is the state a settings page is least allowed to guess at:
        // the list is re-read anyway, and the cause is re-attached to whatever row now stands for the id.
        var fx = new Fixture();
        fx.Host.PreInstall("python-3");
        fx.Host.FailRemove = true;
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RemoveCommand.ExecuteAsync(vm.Toolchains.Single(t => t.Id == "python-3"));

        var row = vm.Toolchains.Single(t => t.Id == "python-3");
        Assert.True(row.IsFailed);
        Assert.Contains("could not be removed", row.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.IsInstalled); // the re-probe still finds it — the page does not pretend otherwise
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CatalogReadFailure_ShouldExplain_NotThrow()
    {
        var fx = new Fixture();
        fx.Host.ThrowOnRun = new InvalidOperationException("the Mainguard environment did not answer");
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasLoadError);
        Assert.False(vm.IsLoading); // never a permanent spinner
        Assert.Contains("did not answer", vm.LoadError);
        Assert.Contains("Refresh", vm.LoadError); // names the next step
    }

    // ---- per-row command enablement (the "visible but not clickable" bug class) -----------------
    //
    // Install/Remove live on the PARENT view model but their CanExecute predicates read ROW state.
    // [NotifyCanExecuteChangedFor] fires only from the parent's own IsBusy, so a row changing raised
    // PropertyChanged (the button turned VISIBLE) while CanExecuteChanged never fired — and a Button
    // caches its last CanExecute result. These pin the NOTIFICATION, which is the half that was missing
    // on the agent-CLI page and shipped dead buttons for eleven days.

    [Fact]
    public void RowBecomingInstallable_ShouldRepublishInstallCanExecute()
    {
        var row = new ToolchainRowViewModel("python-3", "Python 3", "CPython with pip", "3.12.13", isInstalled: true);
        var vm = new ToolchainSettingsViewModel(new[] { row });
        Assert.False(vm.InstallCommand.CanExecute(row));

        var raised = 0;
        vm.InstallCommand.CanExecuteChanged += (_, _) => raised++;
        row.IsInstalled = false;

        Assert.True(vm.InstallCommand.CanExecute(row));
        Assert.True(raised > 0, "InstallCommand never raised CanExecuteChanged when the row became installable — "
            + "the button shows but stays disabled.");
    }

    [Fact]
    public void RowBecomingRemovable_ShouldRepublishRemoveCanExecute()
    {
        var row = new ToolchainRowViewModel("python-3", "Python 3", "CPython with pip", "3.12.13");
        var vm = new ToolchainSettingsViewModel(new[] { row });
        Assert.False(vm.RemoveCommand.CanExecute(row));

        var raised = 0;
        vm.RemoveCommand.CanExecuteChanged += (_, _) => raised++;
        row.IsInstalled = true;

        Assert.True(vm.RemoveCommand.CanExecute(row));
        Assert.True(raised > 0, "RemoveCommand never raised CanExecuteChanged when the row became removable.");
    }

    [Fact]
    public async Task RowsArrivingFromRefresh_ShouldRepublishRowCommandCanExecute()
    {
        // Rows are replaced wholesale by RefreshAsync (Clear + Add). The freshly-added rows must be
        // watched too — otherwise the bridge would only hold for design-constructed lists.
        var fx = new Fixture();
        fx.Host.PreInstall("python-3");
        var vm = new ToolchainSettingsViewModel(fx.CreateChannel());
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Toolchains.Single(t => t.Id == "python-3");
        var raised = 0;
        vm.InstallCommand.CanExecuteChanged += (_, _) => raised++;
        row.IsInstalled = false;

        Assert.True(vm.InstallCommand.CanExecute(row));
        Assert.True(raised > 0, "a row created by RefreshAsync is not watched — its Install button stays dead.");
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Two curated toolchains over a fake VM — the real channel policy (hash verification,
    /// staged extraction, swap-into-place, version-matched probe, marker-written-last) runs unchanged.</summary>
    private sealed class Fixture
    {
        public const string PythonUrl = "https://example.invalid/cpython-3.12.13.tar.gz";
        public const string NodeUrl = "https://example.invalid/node-22.14.0.tar.gz";

        // The REAL hash of the bytes the fake source serves. It has to be real: the channel hashes the
        // payload it is holding, on the host, before the VM sees anything — so a fixture can no longer
        // declare an arbitrary hex and have a fake VM agree with it.
        public static readonly string PythonSha = FakeToolchainPayloadSource.Sha256For(PythonUrl);
        public static readonly string NodeSha = FakeToolchainPayloadSource.Sha256For(NodeUrl);

        public readonly ToolchainManifest Manifest = ToolchainManifest.Parse($$"""
        {
          "toolchains": [
            {
              "id": "python-3",
              "displayName": "Python 3",
              "summary": "CPython 3.12 with pip — runs a repository's Python test suite.",
              "version": "3.12.13",
              "payloadUrl": "https://example.invalid/cpython-3.12.13.tar.gz",
              "sha256": "{{PythonSha}}",
              "payloadUrlArm64": "https://example.invalid/cpython-3.12.13.tar.gz",
              "sha256Arm64": "{{PythonSha}}",
              "stripComponents": 1,
              "pathEntries": ["{toolchain}/bin"],
              "probe": {
                "command": ["{toolchain}/bin/python3", "-c", "import pip, sys; print(sys.version)"],
                "expectedVersionSubstring": "3.12.13"
              }
            },
            {
              "id": "node-22",
              "displayName": "Node.js 22",
              "summary": "Node 22 with npm — runs a repository's JavaScript test suite.",
              "version": "22.14.0",
              "payloadUrl": "https://example.invalid/node-22.14.0.tar.gz",
              "sha256": "{{NodeSha}}",
              "payloadUrlArm64": "https://example.invalid/node-22.14.0.tar.gz",
              "sha256Arm64": "{{NodeSha}}",
              "stripComponents": 1,
              "pathEntries": ["{toolchain}/bin"],
              "probe": {
                "command": ["{toolchain}/bin/node", "--version"],
                "expectedVersionSubstring": "22.14.0"
              }
            }
          ]
        }
        """);

        public readonly FakeHost Host = new();

        public readonly FakeToolchainPayloadSource Payloads = new();

        public Fixture() => Host.Entries = Manifest.Entries;

        public ToolchainChannel CreateChannel() => new(Host, Manifest, payloads: Payloads);
    }

    /// <summary>Fake VM: every command the channel issues, scripted by argv. A toolchain probes green at
    /// its pinned version only once it has actually been moved into place (or pre-installed).</summary>
    private sealed class FakeHost : IAdapterInstallHost
    {
        public IReadOnlyList<ToolchainEntry> Entries = Array.Empty<ToolchainEntry>();

        /// <summary>id → the version its probe prints. Absent = the toolchain is not there.</summary>
        private readonly Dictionary<string, string> _present = new(StringComparer.Ordinal);

        public readonly List<string> Extracted = new();
        public readonly List<string> WrittenFiles = new();

        /// <summary>Payload file names transferred into this fake VM. Empty means nothing crossed.</summary>
        public readonly List<string> Staged = new();

        public bool FailRemove;
        public Exception? ThrowOnRun;

        /// <summary>How many times a toolchain has been RUN — the list's honesty is measured in these.</summary>
        public int Probes;

        public IReadOnlyList<string> InstalledIds => _present.Keys.ToList();

        /// <summary>Seeds a toolchain as already present, optionally reporting some OTHER version than
        /// the pinned one (the "a different Python is installed" case).</summary>
        public void PreInstall(string id, string? reportsVersion = null) =>
            _present[id] = reportsVersion ?? VersionOf(id);

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            if (ThrowOnRun is not null)
                return Task.FromException<AdapterCommandResult>(ThrowOnRun);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Run(command));
        }

        private AdapterCommandResult Run(IReadOnlyList<string> command)
        {
            var argv0 = command[0];

            // A probe: the toolchain's own binary, by absolute path inside its install dir.
            if (argv0.StartsWith(ToolchainPaths.VmRoot + "/", StringComparison.Ordinal))
            {
                Probes++;
                var probed = IdIn(argv0);
                return _present.TryGetValue(probed, out var reported)
                    ? new AdapterCommandResult(0, reported, "")
                    : new AdapterCommandResult(127, "", $"{argv0}: no such file or directory");
            }

            // A real MainguardEnv has neither, and a fake that answered them is how a `curl`-based
            // install path passed this whole file while failing every user. See MainguardEnvFacts.
            if (MainguardEnvFacts.RefuseIfAbsent(argv0) is { } absent)
                return absent;

            var id = IdIn(string.Join(' ', command));
            switch (argv0)
            {
                case "mkdir":
                    return new AdapterCommandResult(0, "", "");

                case "rm":
                    // Only the install-dir removal changes what is present; staging/payload cleanup does not.
                    if (command.Contains(ToolchainPaths.VmInstallDir(id)))
                    {
                        if (FailRemove)
                            return new AdapterCommandResult(1, "", $"rm: cannot remove '{ToolchainPaths.VmInstallDir(id)}': Device or resource busy");
                        _present.Remove(id);
                    }

                    return new AdapterCommandResult(0, "", "");

                case "tar":
                    Extracted.Add(id);
                    return new AdapterCommandResult(0, "", "");

                case "mv":
                    // The swap into place: from here on the toolchain runs at its pinned version.
                    _present[id] = VersionOf(id);
                    return new AdapterCommandResult(0, "", "");

                default:
                    return new AdapterCommandResult(0, "", "");
            }
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            WrittenFiles.Add(path);
            return Task.CompletedTask;
        }

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
        {
            Staged.Add(fileName);
            return Task.FromResult($"{ToolchainPaths.VmStageDir}/{fileName}");
        }

        /// <summary>The curated id named anywhere in a command line (ids are a narrow, unambiguous grammar).</summary>
        private string IdIn(string text) =>
            Entries.FirstOrDefault(e => text.Contains(e.Id, StringComparison.Ordinal))?.Id ?? "";

        private string VersionOf(string id) => Entries.Single(e => e.Id == id).Version;
    }
}
