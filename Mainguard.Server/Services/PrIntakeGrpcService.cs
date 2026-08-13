using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using StoredSettings = Mainguard.Agents.Agents.Orchestrator.PrIntakeSettings;
using WireSettings = Mainguard.Protos.V1.PrIntakeSettings;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="PrIntakeService"/> (P2-12): the external-PR intake's configuration and
/// subscriptions. Validation + dispatch only — the durable state is the daemon's
/// <see cref="IPrIntakeStore"/>, and the poll loop (<see cref="IExternalPrIntake"/>) reads that same
/// store, so what a human saves here is literally what the next poll runs on.
///
/// <para><b>Why this service exists at all.</b> The App shipped a complete intake settings dialog with no
/// way to open it, and the reason it had no way to open it is that it had nowhere real to write: its
/// ViewModel took an <c>IPrIntakeStore</c> and defaulted to an <b>in-process</b> one. Wiring the dialog
/// to that would have produced the defect this repo has spent a week removing — a settings screen that
/// looks saved and changes nothing, because the process that acts on the setting is the daemon and the
/// daemon never saw it. So the configuration moved to where the actor is, and this is the seam the App
/// reaches it through.</para>
///
/// <para><b>Subscribing is a provisioning act, not a preference.</b> A subscribed repository causes the
/// daemon to fetch pull-request heads and ask the gated spawn chain for a jail per PR — which is why
/// both writes here are denied to the coordinator role at <c>RoleInterceptor</c>, alongside the merge
/// RPCs. An agent that could subscribe its own repository could manufacture queue entries and jails.</para>
/// </summary>
public sealed class PrIntakeGrpcService : PrIntakeService.PrIntakeServiceBase
{
    private readonly IPrIntakeStore _store;
    private readonly IExternalPrIntake _intake;
    private readonly ILogger _log;

    public PrIntakeGrpcService(IPrIntakeStore store, IExternalPrIntake intake, ILoggerFactory loggerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Intake);
    }

    public override Task<GetPrIntakeSettingsResponse> GetPrIntakeSettings(
        GetPrIntakeSettingsRequest request, ServerCallContext context)
    {
        var response = new GetPrIntakeSettingsResponse { Settings = ToWire(_store.GetSettings()) };
        response.Sources.AddRange(_store.Subscriptions().Select(ToWire));
        return Task.FromResult(response);
    }

    public override Task<UpdatePrIntakeSettingsResponse> UpdatePrIntakeSettings(
        UpdatePrIntakeSettingsRequest request, ServerCallContext context)
    {
        var incoming = request.Settings ?? new WireSettings();
        _store.SaveSettings(new StoredSettings(
            incoming.Enabled,
            incoming.PollIntervalSeconds,
            incoming.BotAuthors.ToList()));

        // Read BACK rather than echo the request: the store clamps the interval and substitutes the
        // default bot list for an empty one, and the page must render what the daemon will actually poll
        // with. Echoing the request would show the user a cadence the poller is not using — a smaller,
        // quieter version of the same lie this whole surface exists to avoid.
        var persisted = _store.GetSettings();
        _log.LogInformation(
            "external-PR intake settings updated: enabled={Enabled} interval={Interval}s authors={Authors}",
            persisted.Enabled, persisted.PollIntervalSeconds, persisted.BotAuthors.Count);

        return Task.FromResult(new UpdatePrIntakeSettingsResponse { Settings = ToWire(persisted) });
    }

    public override Task<SubscribePrIntakeSourceResponse> SubscribePrIntakeSource(
        SubscribePrIntakeSourceRequest request, ServerCallContext context)
    {
        var wire = request.Source ?? new PrIntakeSource();
        if (string.IsNullOrWhiteSpace(wire.Host) || string.IsNullOrWhiteSpace(wire.Owner)
            || string.IsNullOrWhiteSpace(wire.Repo))
        {
            // A refusal, not a crash: an incomplete source would persist a row that can never resolve to
            // a repository and would be skipped silently on every poll forever.
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "a subscription needs a host, an owner and a repository"));
        }

        var source = new ExternalPrSource(
            wire.Host.Trim(), wire.Owner.Trim(), wire.Repo.Trim(),
            string.IsNullOrWhiteSpace(wire.AuthorFilter) ? null : wire.AuthorFilter.Trim());

        // Persist, then tell the LIVE engine about it — the same order GatewayService.SetBudgets uses.
        // Subscribe is idempotent on the store, so the running engine and the store cannot disagree, and
        // the source starts being polled without waiting for a daemon restart to re-seed it.
        var added = _store.AddSubscription(source);
        _intake.Subscribe(source);

        if (added)
        {
            _log.LogInformation("external-PR intake subscribed {Source}", source.Key);
        }

        var response = new SubscribePrIntakeSourceResponse { Added = added };
        response.Sources.AddRange(_store.Subscriptions().Select(ToWire));
        return Task.FromResult(response);
    }

    private static WireSettings ToWire(StoredSettings stored)
    {
        var wire = new WireSettings
        {
            Enabled = stored.Enabled,
            PollIntervalSeconds = stored.PollIntervalSeconds,
        };
        wire.BotAuthors.AddRange(stored.BotAuthors);
        return wire;
    }

    private static PrIntakeSource ToWire(ExternalPrSource source) => new()
    {
        Host = source.Host,
        Owner = source.Owner,
        Repo = source.Repo,
        AuthorFilter = source.AuthorFilter ?? string.Empty,
    };
}
