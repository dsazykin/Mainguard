using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One curated language toolchain as presented to the user in Settings → Toolchains. Projects a
/// <see cref="ToolchainStatus"/> from the <see cref="ToolchainChannel"/> plus its install/remove
/// lifecycle, exactly as <see cref="AgentCliRowViewModel"/> does for agent CLIs — same shape on
/// purpose, so the two "the user installs it, into the VM, at a pinned version" surfaces cannot drift.
///
/// <para>State is encoded for the View as booleans so a row always renders as icon AND text, never
/// colour alone. <see cref="Detail"/> carries what the PROBE reported (or why it did not run), which is
/// the only reason a row can say "installed" at all: the channel re-runs the toolchain on every list
/// rather than trusting a marker file, and a toolchain that runs at the WRONG version comes back here
/// as not-installed with the mismatch spelled out.</para>
/// </summary>
public partial class ToolchainRowViewModel : ViewModelBase
{
    /// <summary>Live constructor: one status straight off the channel.</summary>
    public ToolchainRowViewModel(ToolchainStatus status)
        : this(status.Entry.Id, status.Entry.DisplayName, status.Entry.Summary, status.Entry.Version,
               status.IsInstalled, status.Detail)
    {
    }

    /// <summary>Design/harness constructor: fixed representative state, no channel behind it.</summary>
    public ToolchainRowViewModel(
        string id, string displayName, string summary, string version,
        bool isInstalled = false, string? detail = null)
    {
        Id = id;
        DisplayName = displayName;
        Summary = summary;
        Version = version;
        _isInstalled = isInstalled;
        _detail = detail;
    }

    /// <summary>The manifest id — the same token a repository's <c>.mainguard/toolchain</c> may name,
    /// and the directory the toolchain installs into inside the VM.</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>One line saying what this toolchain gives a jail.</summary>
    public string Summary { get; }

    /// <summary>The pinned version the channel installs — concrete by construction.</summary>
    public string Version { get; }

    /// <summary>The version chip text (<c>v3.12.13</c>).</summary>
    public string VersionLabel => $"v{Version}";

    /// <summary>True only once the toolchain has been RUN at the pinned version — never because a
    /// marker file exists. Flipped by the channel's probe on refresh/install, and back by Remove.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ShowInstalledDetail))]
    private bool _isInstalled;

    /// <summary>An install or a removal is in flight on THIS row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isWorking;

    /// <summary>What the probe reported, or why it did not run — including the "a different version is
    /// present" case, which is exactly the sentence a user needs and an exit code is not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(NotInstalledDetail))]
    [NotifyPropertyChangedFor(nameof(ShowInstalledDetail))]
    private string? _detail;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>The not-installed line. The channel's own wording when it has some (a wrong version
    /// present, say); a plain "Not installed" otherwise. Never both, so the row never repeats itself.</summary>
    public string NotInstalledDetail => HasDetail ? Detail! : "Not installed";

    /// <summary>An installed row also shows what the probe printed — the proof behind the claim.</summary>
    public bool ShowInstalledDetail => IsInstalled && HasDetail;

    /// <summary>The last install/remove attempt failed; <see cref="StatusMessage"/> carries the typed
    /// channel refusal (checksum mismatch, unpack failure, probe failure, version mismatch).</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>Progress or failure detail under the row. Null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>Not installed, nothing in flight — the row offers Install in this state.</summary>
    public bool CanInstall => !IsInstalled && !IsWorking;

    /// <summary>Installed, nothing in flight — the row offers Remove in this state.</summary>
    public bool CanRemove => IsInstalled && !IsWorking;

    /// <summary>No lifecycle activity at all (the neutral glyph).</summary>
    public bool IsIdle => !IsInstalled && !IsWorking;
}
