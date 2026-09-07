using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Git;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Security;
using Mainguard.UI.ViewModels;
namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// AI Providers preferences page (P2-01): store a BYOK LLM API key per provider, keyed
/// <c>llm_&lt;provider&gt;</c> in the OS keyring. Save is <b>validate-then-store</b> — the key is
/// health-checked off the UI thread first (<see cref="ApiKeyHealthService"/>) and an invalid key is
/// never persisted (invariant 3). The candidate key lives in a plain string only while the page is open
/// and is nulled after every save/health-check (invariant 1). The CLI-OAuth path shows the Anthropic
/// ToS notice before it can activate.
///
/// <para>Constructed directly (no DI). The keyring, the health-check delegate, and the db factory are
/// injectable seams so the VM is fully unit-testable offline.</para>
/// </summary>
public partial class ApiKeySettingsViewModel : ViewModelBase, ISettingsPage
{
    private readonly ISecureKeyStore _keyStore;
    private readonly Func<string, string, CancellationToken, Task<KeyHealth>> _healthCheck;
    private readonly Func<AppDbContext> _dbFactory;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The providers offered in the dropdown — one per installable CLI's declared env
    /// contract (claude-code, codex, gemini). Static array so <c>llm_&lt;provider&gt;</c> extends
    /// without UI rework; anything else goes through the custom env-var section below.</summary>
    public string[] AvailableProviders { get; } = { "anthropic", "openai", "google" };

    public ObservableCollection<ApiKeyProviderRowViewModel> Providers { get; } = new();

    /// <summary>The user's custom env-var keys (<c>llm_env_*</c>): BYOK for multi-provider CLIs
    /// like opencode that declare no <c>apiKeyEnvVar</c> of their own.</summary>
    public ObservableCollection<CustomApiKeyRowViewModel> CustomKeys { get; } = new();

    [ObservableProperty]
    private string _selectedProvider = "anthropic";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _healthMessage;

    [ObservableProperty]
    private bool _isHealthError;

    [ObservableProperty]
    private bool _isCliOAuthEnabled;

    /// <summary>Set by the View to present the modal ToS dialog; returns true when acknowledged.</summary>
    public Func<string, Task<bool>>? ShowTosDialogAsync { get; set; }

    /// <summary>Wired from the View so Close works from the ViewModel.</summary>
    public Action? CloseAction { get; set; }

    public ApiKeySettingsViewModel(
        ISecureKeyStore? keyStore = null,
        Func<string, string, CancellationToken, Task<KeyHealth>>? healthCheck = null,
        Func<AppDbContext>? dbFactory = null)
    {
        _keyStore = keyStore ?? new SecureKeyring();
        _healthCheck = healthCheck ?? new ApiKeyHealthService().CheckAsync;
        _dbFactory = dbFactory ?? (() => new AppDbContext());
        RefreshRows();
    }

    /// <summary>
    /// <b>F51 — what actually happens to a stored key, said on the page that stores it.</b>
    ///
    /// <para>Mainguard can keep a provider key OUT of the jail only when the CLI can be pointed
    /// somewhere else: the spawn path fronts it with the model gateway and gives the container an
    /// <c>mg_sess_</c> token instead of the key. A CLI that declares no base-URL variable and no model
    /// host cannot be pointed anywhere, so the RAW KEY is written into its jail and its spend is not
    /// metered. That is a property of the vendor's CLI, not a misconfiguration — but until now the only
    /// place it was said was a daemon log line, while the page that took the key said nothing.</para>
    ///
    /// <para>Read out of the SHIPPED manifest rather than from a table kept by hand here, because a
    /// hand-kept copy of "which CLIs can be fronted" is a claim about security that goes stale the day
    /// a vendor adds a base-URL variable — and it goes stale silently, in the reassuring direction.</para>
    ///
    /// <para>Null when the answer is not known (a manifest that will not parse). The caller then shows
    /// the warning anyway: a warning shown when a key would in fact have been confined costs the reader
    /// a moment, and the opposite mistake is the finding.</para>
    /// </summary>
    internal static bool? ProviderCanBeGatewayConfined(string provider)
    {
        try
        {
            var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
            var forProvider = manifest.Adapters
                .Where(a => string.Equals(
                    Services.ApiKeyProviderMap.ProviderForEnvVar(a.ApiKeyEnvVar), provider, StringComparison.Ordinal))
                .ToArray();

            if (forProvider.Length == 0)
            {
                return null;
            }

            // ALL of them, not any: the key is stored per provider and every CLI that reads it gets it,
            // so the honest answer for the provider is the weakest of its CLIs.
            return forProvider.All(a =>
                !string.IsNullOrWhiteSpace(a.BaseUrlEnvVar) && !string.IsNullOrWhiteSpace(a.ModelHost));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The sentence shown for a provider whose CLI cannot be fronted, or null when it can.
    /// One place, so the save message and the stored-key row cannot describe the same key differently.</summary>
    internal static string? ConfinementNoticeFor(string provider) =>
        ProviderCanBeGatewayConfined(provider) == true
            ? null
            : "This CLI cannot be pointed at Mainguard's model gateway, so this key is written into the "
              + "agent's sandbox as-is and its spend is not metered. Anything running in that sandbox can read it.";

    private void RefreshRows()
    {
        Providers.Clear();
        foreach (var provider in AvailableProviders)
        {
            var hasKey = !string.IsNullOrEmpty(_keyStore.Get($"llm_{provider}"));
            Providers.Add(new ApiKeyProviderRowViewModel(this, provider, hasKey));
        }

        CustomKeys.Clear();
        foreach (var keystoreKey in _keyStore.List(Services.ApiKeyProviderMap.CustomEnvKeyPrefix))
        {
            if (Services.ApiKeyProviderMap.EnvVarForCustomKey(keystoreKey) is { } envVar)
            {
                CustomKeys.Add(new CustomApiKeyRowViewModel(this, envVar));
            }
        }
    }

    private bool CanSave => !IsBusy && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SelectedProvider);
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnApiKeyChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>Validate-then-store: health-check off the UI thread; store only when valid; then null the
    /// candidate key. A transport/unknown-provider failure surfaces inline and stores nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var provider = SelectedProvider;
        var key = ApiKey; // captured before the field is cleared
        IsBusy = true;
        IsHealthError = false;
        HealthMessage = "Checking key…";
        try
        {
            var health = await Task.Run(() => _healthCheck(provider, key, _cts.Token), _cts.Token);
            if (health.IsValid)
            {
                _keyStore.Set($"llm_{provider}", key);
                IsHealthError = false;
                // F51: say where the key ends up, at the moment it is taken. The confinable case says
                // nothing extra — a page that warns about everything is a page nobody reads.
                var notice = ConfinementNoticeFor(provider);
                HealthMessage = $"Key valid — supports ~{health.EstimatedConcurrentAgents} concurrent agents."
                    + (notice is null ? string.Empty : " " + notice);
                RefreshRows();
            }
            else
            {
                // Invalid key is NOT stored (invariant 3).
                IsHealthError = true;
                HealthMessage = health.FailureReason ?? "The provider rejected this key.";
            }
        }
        catch (OperationCanceledException)
        {
            // Page closed mid-check — nothing stored, no message churn.
        }
        catch (MainguardException ex)
        {
            // Unreachable / unknown provider: typed failure — nothing stored, retry affordance stays.
            IsHealthError = true;
            HealthMessage = ex.Message;
        }
        catch (Exception ex)
        {
            IsHealthError = true;
            HealthMessage = ex.Message;
        }
        finally
        {
            ApiKey = string.Empty; // null out the candidate after the check completes (invariant 1)
            IsBusy = false;
        }
    }

    internal void DeleteKey(string provider)
    {
        _keyStore.Delete($"llm_{provider}");
        IsHealthError = false;
        HealthMessage = $"Removed the stored {provider} key.";
        RefreshRows();
    }

    // ---- custom env-var keys (opencode-style multi-provider CLIs) ----

    [ObservableProperty]
    private string _customEnvVarName = string.Empty;

    [ObservableProperty]
    private string _customApiKey = string.Empty;

    /// <summary>Env-var names are the POSIX identifier shape; anything else would corrupt the
    /// agent env-file. Uppercase is convention but not enforced.</summary>
    internal static bool IsValidEnvVarName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    private bool CanAddCustomKey =>
        !IsBusy && !string.IsNullOrWhiteSpace(CustomEnvVarName) && !string.IsNullOrWhiteSpace(CustomApiKey);

    partial void OnCustomEnvVarNameChanged(string value) => AddCustomKeyCommand.NotifyCanExecuteChanged();
    partial void OnCustomApiKeyChanged(string value) => AddCustomKeyCommand.NotifyCanExecuteChanged();

    /// <summary>Stores a custom env-var key (<c>llm_env_&lt;NAME&gt;</c>). No health check — the
    /// endpoint behind a custom variable is unknown by definition; the page says so honestly.</summary>
    [RelayCommand(CanExecute = nameof(CanAddCustomKey))]
    private void AddCustomKey()
    {
        var name = CustomEnvVarName.Trim();
        if (!IsValidEnvVarName(name))
        {
            IsHealthError = true;
            HealthMessage = "Environment variable names must look like OPENROUTER_API_KEY (letters, digits, underscores; no leading digit).";
            return;
        }

        _keyStore.Set(Services.ApiKeyProviderMap.CustomEnvKeyPrefix + name, CustomApiKey);
        CustomApiKey = string.Empty; // null the candidate immediately (invariant 1)
        CustomEnvVarName = string.Empty;
        IsHealthError = false;
        // "every agent" was an overstatement the code never made (ISSUES-LOG #37 held this sentence up
        // as the contract and it is the one claim here that is not literally true): a jail running an
        // EXTERNAL pull request's code is spawned withoutHostCredentials and deliberately inherits none
        // of these — see AgentSpawnService.SpawnAsync. Say the exception rather than let a reader who
        // checks it conclude the whole feature is broken.
        // F51: a custom env-var key is by definition one no adapter declares, so the gateway has no
        // base-URL variable to redirect and no model host to forward to. It is ALWAYS written into the
        // jail as-is and never metered — there is no confinable case here to leave unsaid.
        HealthMessage = $"Stored {name} (custom keys are stored without provider validation) — it is injected into the environment of every agent you start, except jails running an external pull request's code. "
            + "Mainguard cannot front a custom variable with its model gateway, so this key is written into the agent's sandbox as-is and its spend is not metered.";
        RefreshRows();
    }

    internal void DeleteCustomKey(string envVarName)
    {
        _keyStore.Delete(Services.ApiKeyProviderMap.CustomEnvKeyPrefix + envVarName);
        IsHealthError = false;
        HealthMessage = $"Removed the stored {envVarName} key.";
        RefreshRows();
    }

    /// <summary>CLI-OAuth path: show the Anthropic ToS notice (unless already acknowledged) before it can
    /// activate. Cancel leaves the option off.</summary>
    [RelayCommand]
    private async Task UseClaudeSubscription()
    {
        const string provider = "anthropic";
        bool acknowledged;
        using (var db = _dbFactory())
            acknowledged = db.HasTosAcknowledgment(provider);

        if (!acknowledged)
        {
            acknowledged = ShowTosDialogAsync is not null && await ShowTosDialogAsync(provider);
            if (!acknowledged)
            {
                IsHealthError = false;
                HealthMessage = "Claude subscription (CLI OAuth) was not enabled.";
                return;
            }
        }

        IsCliOAuthEnabled = true;
        IsHealthError = false;
        // Honest about what just happened: the acknowledgment is recorded (and the badge shows), but
        // no spawn path reads this flag — authentication happens in the CLI's own terminal when no
        // API key is stored, and the harvested login is restored into new jails. This button GATES
        // nothing; pretending otherwise was the B9 finding.
        HealthMessage = "Terms acknowledged. Start a coordinator and sign in inside its terminal — "
            + "the login is saved to your keychain and restored into new jails. The API-key path stays recommended.";
    }

    /// <summary>Called by the View when the page closes: cancel any in-flight health check.</summary>
    public void CancelPendingWork()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
    }

    [RelayCommand]
    private void Close()
    {
        CancelPendingWork();
        CloseAction?.Invoke();
    }

    public void OnActivated()
    {
    }

    /// <summary>As a Settings page: cancel any in-flight health check on navigating away (replaces
    /// the old Window.Closed hook).</summary>
    public void OnDeactivated() => CancelPendingWork();
}

/// <summary>One provider row on the AI Providers page: stored-key status + a per-provider delete.</summary>
public partial class ApiKeyProviderRowViewModel : ViewModelBase
{
    private readonly ApiKeySettingsViewModel _parent;

    public string Provider { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _hasKey;

    public ApiKeyProviderRowViewModel(ApiKeySettingsViewModel parent, string provider, bool hasKey)
    {
        _parent = parent;
        Provider = provider;
        DisplayName = provider switch
        {
            "anthropic" => "Anthropic (Claude)",
            "openai" => "OpenAI",
            "google" => "Google (Gemini)",
            _ => provider,
        };
        _hasKey = hasKey;
    }

    public string StatusLabel => HasKey ? "Key stored" : "No key stored";

    /// <summary>F51: the sentence about where this provider's key ends up, or empty when its CLI can be
    /// fronted by the gateway. On the ROW as well as in the save message, because the save message is
    /// gone by the next time the user opens this page and the key is still in the jail.</summary>
    public string ConfinementNotice =>
        ApiKeySettingsViewModel.ConfinementNoticeFor(Provider) ?? string.Empty;

    /// <summary>Shown only for a stored key that will really travel — an unconfinable provider the user
    /// has not given a key to has nothing to warn about yet.</summary>
    public bool ShowsConfinementNotice => HasKey && ConfinementNotice.Length > 0;

    [RelayCommand]
    private void Delete() => _parent.DeleteKey(Provider);
}

/// <summary>One stored custom env-var key (<c>llm_env_*</c>): the variable name + a delete.</summary>
public partial class CustomApiKeyRowViewModel : ViewModelBase
{
    private readonly ApiKeySettingsViewModel _parent;

    public string EnvVarName { get; }

    public CustomApiKeyRowViewModel(ApiKeySettingsViewModel parent, string envVarName)
    {
        _parent = parent;
        EnvVarName = envVarName;
    }

    [RelayCommand]
    private void Delete() => _parent.DeleteCustomKey(EnvVarName);
}
