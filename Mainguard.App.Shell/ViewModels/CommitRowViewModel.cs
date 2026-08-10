using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Git.Graph;
using Mainguard.Git.Models;

namespace Mainguard.App.Shell.ViewModels;

public partial class CommitRowViewModel : ObservableObject
{
    public GitCommitItem Commit { get; set; } = null!;
    public GraphNode Node { get; set; } = null!;

    /// <summary>
    /// Branch/tag chips sitting at this commit (ref tips), rendered inline in the row and used as
    /// the drag source/target for the T-09 drag-to-rebase/merge gesture. Empty for most rows.
    /// </summary>
    public ObservableCollection<RefLabelViewModel> RefLabels { get; } = new();

    /// <summary>True when this row carries at least one ref chip (collapses the holder otherwise).</summary>
    public bool HasRefLabels => RefLabels.Count > 0;

    /// <summary>Re-raises <see cref="HasRefLabels"/> after the chips were rebuilt in place (the
    /// timeline's "Tag Names" toggle re-decorates loaded rows rather than reloading them).</summary>
    public void NotifyRefLabelsChanged() => OnPropertyChanged(nameof(HasRefLabels));

    [ObservableProperty]
    private bool _isHighlighted;

    // Signature verification (T-15). Set only when the timeline's signature column is on; the
    // badge in the row template toggles off these derived booleans. Default None → no badge.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignatureVerified))]
    [NotifyPropertyChangedFor(nameof(IsSignatureUntrusted))]
    [NotifyPropertyChangedFor(nameof(IsSignatureBad))]
    [NotifyPropertyChangedFor(nameof(HasSignatureBadge))]
    [NotifyPropertyChangedFor(nameof(SignatureTooltip))]
    private SignatureStatus _signatureStatus = SignatureStatus.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureTooltip))]
    private string _signatureSigner = string.Empty;

    /// <summary>Any signature present — collapses the badge holder (no reserved width) when false.</summary>
    public bool HasSignatureBadge => SignatureStatus != SignatureStatus.None;

    /// <summary>Good signature from a trusted key (git <c>G</c>) — the green verified badge.</summary>
    public bool IsSignatureVerified => SignatureStatus == SignatureStatus.Good;

    /// <summary>Signed but not fully verified (unknown validity, expired, can't-check) — amber badge.</summary>
    public bool IsSignatureUntrusted =>
        SignatureStatus is SignatureStatus.UnknownValidity
            or SignatureStatus.Expired
            or SignatureStatus.ExpiredKey
            or SignatureStatus.CannotCheck;

    /// <summary>Bad or revoked signature — the red warning badge.</summary>
    public bool IsSignatureBad =>
        SignatureStatus is SignatureStatus.Bad or SignatureStatus.Revoked;

    public string SignatureTooltip
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(SignatureSigner) ? null : $" · {SignatureSigner}";
            return SignatureStatus switch
            {
                SignatureStatus.Good => $"Verified signature{who}",
                SignatureStatus.UnknownValidity => $"Signed — key validity unknown{who}",
                SignatureStatus.Expired => $"Signed — signature expired{who}",
                SignatureStatus.ExpiredKey => $"Signed — key expired{who}",
                SignatureStatus.CannotCheck => "Signed — cannot verify (public key missing)",
                SignatureStatus.Bad => "Bad signature — content does not match",
                SignatureStatus.Revoked => $"Signed — key revoked{who}",
                _ => "Not signed",
            };
        }
    }
}
