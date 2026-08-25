using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.UI.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The DEV-ONLY queue-seeding panel (docs/design/queue-seeding.md §6–7). Exists at all only when the
/// daemon answered the availability probe — a daemon without the seeding boot flag never maps the
/// service and <c>ControlCenterViewModel</c> leaves this null, so the card is absent, not disabled.
///
/// <para>The panel is a thin driver over the RPC primitives; the scenario presets are CLIENT-side
/// compositions of those primitives (a stale pair is literally two specs in one ordered batch), so
/// the daemon surface stays small and every call remains an honest state-machine drive. Refusals are
/// rendered verbatim — the daemon's words, never a client paraphrase.</para>
/// </summary>
public partial class QueueSeedingPanelViewModel : ViewModelBase
{
    private readonly IQueueSeedingGateway _gateway;

    /// <summary>The wire vocabulary, verbatim (SeedEntrySpec.target_state).</summary>
    public IReadOnlyList<string> TargetStates { get; } = new[]
    {
        "Working", "Verifying", "Verified", "StaleVerified", "AwaitingReview", "Merged", "Rejected", "Discarded",
    };

    /// <summary>The wire vocabulary, verbatim (SeedEntrySpec.flavor).</summary>
    public IReadOnlyList<string> Flavors { get; } = new[] { "PLAIN", "FLAGGED", "CHANGED_TEST_COMMAND" };

    [ObservableProperty] private string _selectedState = "Verified";
    [ObservableProperty] private string _selectedFlavor = "PLAIN";
    [ObservableProperty] private int _count = 1;
    [ObservableProperty] private bool _verificationFails;
    [ObservableProperty] private int _holdSeconds = 60;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>The most recently seeded id — the target of the Push-commits action.</summary>
    [ObservableProperty] private string _lastSeededId = "";

    public QueueSeedingPanelViewModel(IQueueSeedingGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    [RelayCommand]
    private Task SeedAsync() => RunBatchAsync("Seeded", new[]
    {
        new SeedEntryRequestItem(
            TargetState: SelectedState,
            Count: Math.Max(1, Count),
            Flavor: SelectedFlavor,
            VerificationFails: VerificationFails,
            HoldSeconds: SelectedState == "Verifying" ? Math.Max(1, HoldSeconds) : 0),
    });

    /// <summary>[Verified, Merged] — the second spec's REAL merge stales the first (design §5).</summary>
    [RelayCommand]
    private Task SeedStalePairAsync() => RunBatchAsync("Stale pair", new[]
    {
        new SeedEntryRequestItem("Verified"),
        new SeedEntryRequestItem("Merged"),
    });

    /// <summary>One entry in every seedable shape — the full-rail rendering pass in one click.</summary>
    [RelayCommand]
    private Task SeedOneOfEachAsync() => RunBatchAsync("One of each", new[]
    {
        new SeedEntryRequestItem("Working"),
        new SeedEntryRequestItem("Working", VerificationFails: true), // the verify-FAIL record
        new SeedEntryRequestItem("Verified"),
        new SeedEntryRequestItem("Verified", Flavor: "FLAGGED"),      // real must-ack items
        new SeedEntryRequestItem("AwaitingReview"),
        new SeedEntryRequestItem("Rejected", Reason: "seeded review verdict"),
        new SeedEntryRequestItem("Discarded", Reason: "seeded housekeeping"),
    });

    /// <summary>Overflow/ordering reproduction: a dozen verified entries at once.</summary>
    [RelayCommand]
    private Task SeedOverflowAsync() => RunBatchAsync("Overflow ×12",
        new[] { new SeedEntryRequestItem("Verified", Count: 12) });

    /// <summary>The reproducible race real agents can never time (design §3): one entry HELD mid-run
    /// while a sibling's real merge fires the real cascade over it.</summary>
    [RelayCommand]
    private Task SeedMergeDuringVerifyAsync() => RunBatchAsync("Merge during verify", new[]
    {
        new SeedEntryRequestItem("Verifying", HoldSeconds: 120),
        new SeedEntryRequestItem("Verified"),
        new SeedEntryRequestItem("Merged"),
    });

    /// <summary>Appends a real commit to the last-seeded entry — the real invalidation follows.</summary>
    [RelayCommand]
    private async Task PushCommitsAsync()
    {
        if (LastSeededId.Length == 0)
        {
            StatusText = "Nothing seeded yet this session — seed an entry first.";
            return;
        }

        await GuardedAsync(async () =>
        {
            var result = await _gateway.PushCommitsAsync(LastSeededId);
            StatusText = result.Refusal.Length > 0
                ? $"Push refused: {result.Refusal}"
                : $"Pushed to {result.AgentId} — now {result.ReachedState}.";
        });
    }

    [RelayCommand]
    private async Task ClearSeededAsync()
    {
        await GuardedAsync(async () =>
        {
            var (cleared, failures) = await _gateway.ClearAsync();
            LastSeededId = "";
            StatusText = failures.Count == 0
                ? $"Cleared {cleared.Count} seeded entr{(cleared.Count == 1 ? "y" : "ies")}."
                : $"Cleared {cleared.Count}; refused: "
                    + string.Join(" · ", failures.Select(f => $"{f.AgentId}: {f.Refusal}"));
        });
    }

    private async Task RunBatchAsync(string label, IReadOnlyList<SeedEntryRequestItem> entries)
    {
        await GuardedAsync(async () =>
        {
            var batch = await _gateway.SeedAsync(entries);
            var ok = batch.Results.Where(r => r.Refusal.Length == 0).ToList();
            var refused = batch.Results.Where(r => r.Refusal.Length > 0).ToList();
            LastSeededId = ok.LastOrDefault()?.AgentId ?? LastSeededId;

            var summary = $"{label}: {ok.Count} seeded"
                + (ok.Count > 0 ? $" ({string.Join(", ", ok.Select(r => $"{Short(r.AgentId)} {r.ReachedState}"))})" : "");
            if (batch.ProvisionedVerifyConfig)
            {
                summary += " · committed .mainguard/verify to origin main (the repo had none)";
            }

            if (refused.Count > 0)
            {
                summary += " · refused: " + string.Join(" · ", refused.Select(r => $"{Short(r.AgentId)}: {r.Refusal}"));
            }

            StatusText = summary;
        });
    }

    private async Task GuardedAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Grpc.Core.RpcException ex)
        {
            // The daemon's own refusal text, verbatim.
            StatusText = ex.Status.Detail;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Short(string agentId) =>
        agentId.Length > 13 ? agentId[..13] : agentId;
}
