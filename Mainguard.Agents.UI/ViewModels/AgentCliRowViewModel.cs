using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One agent CLI as presented to the user — shared by the OOBE "choose your agents" step (checkbox
/// multi-select) and the Agent CLIs settings window (per-row Install). Projects an
/// <see cref="AgentCliOption"/> from the pinned channel plus its live install lifecycle. State is
/// encoded for the view as booleans so status always renders as icon AND text, never colour alone.
/// </summary>
public partial class AgentCliRowViewModel : ViewModelBase
{
    public AgentCliRowViewModel(AgentCliOption option)
        : this(option.Id, option.DisplayName, option.Version, option.IsInstalled, option.InstalledVersion)
    {
    }

    /// <summary>Design/harness constructor: fixed representative state, no service behind it.</summary>
    public AgentCliRowViewModel(
        string id, string displayName, string version, bool isInstalled = false, string? installedVersion = null)
    {
        Id = id;
        DisplayName = displayName;
        Version = version;
        _isInstalled = isInstalled;
        _installedVersion = installedVersion;
    }

    /// <summary>The channel adapter id (== the daemon's <c>agentKind</c>, e.g. <c>claude-code</c>).</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>The pinned version the channel installs — concrete by construction (never @latest).</summary>
    public string Version { get; }

    /// <summary>The version chip text — what is actually RUNNING when something is installed
    /// (<c>v2.1.223</c>), otherwise the version Install would place (<c>v2.1.218</c>).</summary>
    public string VersionLabel => $"v{InstalledVersion ?? Version}";

    /// <summary>OOBE picker checkbox state. Unused by the settings window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private bool _isSelected;

    /// <summary>True once the health probe reports a runnable CLI — "installed" means "installed AND
    /// runnable", at whatever version is actually there, never just "npm exited 0". It deliberately
    /// does NOT require the pinned version: a copy that drifted ahead of (or behind) this build's pin
    /// is installed, and says so through <see cref="InstalledVersion"/> / <see cref="InstalledLabel"/>
    /// rather than being reported as absent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(HasVersionDrift))]
    [NotifyPropertyChangedFor(nameof(InstalledLabel))]
    private bool _isInstalled;

    /// <summary>The version the probe actually found in the VM; null when nothing is installed (or an
    /// install just finished and the row has not been re-probed yet, which falls back to the pin).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLabel))]
    [NotifyPropertyChangedFor(nameof(HasVersionDrift))]
    [NotifyPropertyChangedFor(nameof(InstalledLabel))]
    private string? _installedVersion;

    /// <summary>The installed copy is a DIFFERENT version from the one this build pins. Normal, not a
    /// fault: installs resolve the registry's current release and an app update can move the pin under
    /// an existing install. Worth stating on the row; never a reason to call the CLI missing.</summary>
    public bool HasVersionDrift =>
        IsInstalled && InstalledVersion is { Length: > 0 } v && !string.Equals(v, Version, StringComparison.Ordinal);

    /// <summary>The green line under an installed row — names the version actually running whenever it
    /// is not the pinned one, so "Installed" and "v2.1.218 offered" can never look contradictory.</summary>
    public string InstalledLabel => HasVersionDrift
        ? $"Installed — v{InstalledVersion} is what runs here; this Mainguard build pins v{Version}"
        : "Installed — verified at the pinned version";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    private bool _isInstalling;

    /// <summary>A newer registry release the user can move to (the Mainguard-managed updater —
    /// in-CLI self-update is disabled in the jails). Null = up to date, or not checked yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateLabel))]
    [NotifyPropertyChangedFor(nameof(UpdateHint))]
    private string? _updateAvailableVersion;

    /// <summary>The under-title hint line ("Update available: v2.1.220").</summary>
    public string UpdateHint => $"Update available: v{UpdateAvailableVersion}";

    /// <summary>The pin an accepted update replaced; Revert restores it (the escape hatch when a
    /// new CLI release breaks the app). Null = nothing to revert to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(RevertLabel))]
    private string? _previousVersion;

    public bool HasUpdate => IsInstalled && !IsInstalling && !string.IsNullOrEmpty(UpdateAvailableVersion);

    public string UpdateLabel => $"Update to v{UpdateAvailableVersion}";

    public bool HasPrevious => IsInstalled && !IsInstalling && !string.IsNullOrEmpty(PreviousVersion);

    public string RevertLabel => $"Revert to v{PreviousVersion}";

    /// <summary>The last install attempt failed; <see cref="StatusMessage"/> carries the actionable
    /// cause (from the typed channel refusal — hash mismatch, in-VM install failure, probe failure).</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>Progress or failure detail under the row. Null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>The OOBE checkbox is live only while there is something to decide.</summary>
    public bool CanSelect => !IsInstalled && !IsInstalling;

    /// <summary>Not installed, not installing — the settings row offers Install in this state.</summary>
    public bool CanInstall => !IsInstalled && !IsInstalling;

    /// <summary>No lifecycle activity at all (pending dot in the OOBE list).</summary>
    public bool IsIdle => !IsInstalled && !IsInstalling;
}
