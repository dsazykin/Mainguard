<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `Mainguard.UI/` (design-system base layer)

**Step 2c (ADR-0001) pulled the edition-agnostic design system down out of `Mainguard.App.Shell`
into `Mainguard.UI`** — the base UI layer the shell (and, from step 2d, the Pro UI) renders on.
Avalonia-only (+ LiveChartsCore for `ChartTheme`); it references NOTHING above it (no
Mainguard.Git/Agents, Mainguard.App.Shell, Docker, Grpc, Protos). The moved types were normalized to
`Mainguard.UI.*` CLR namespaces in 2g — `Mainguard.UI.Controls`/`.Views`/`.Converters` are split
across three assemblies (base ones exposed prefix-free via `XmlnsDefinition`), half in the shell/Pro
UI); only the assembly + its `avares://Mainguard.UI/…` resource URIs are new.

**Step 2e physically split the Pro agent-platform UI out of `Mainguard.App.Shell` into the new
`Mainguard.Agents.UI` assembly** (Avalonia +
`Mainguard.UI`/`Mainguard.Agents`/`Mainguard.Git`/`Mainguard.Protos`; NEVER `Mainguard.App.Shell` —
a strictly one-way boundary the shell references for the Pro-default manifest). It carries every
Pro-only View/VM (Control Center, Coordinator, Resources monitor, agent rail, telemetry, queue rail,
merge queue, review cockpit, agent workspace/document, terminal + `TerminalControl`/`VtScreen`, OOBE
wizard, bootstrap, egress/PR-intake settings, vibe mode, startup/shutdown windows, CLI-OAuth ToS,
VM-upgrade offer), the five Pro Settings pages (`AgentCliSettingsView`, `ApiKeySettingsView`,
`DaemonLogsView`, `ToolchainSettingsView` + `ToolchainDeclarationView` (the user-managed language-toolchain channel's install/
remove surface, `ToolchainSettingsViewModel`/`ToolchainRowViewModel`),
`MainguardOsPageView`/`MainguardOsPageViewModel` — the latter replacing the old
standalone `AddReposToOsView` window and absorbing the former Tools "Rebuild sandbox images" action
as `RebuildSandboxImagesCommand`; the others' root changed from `Window`/`ChromedWindow` to
`UserControl` so they embed in the Settings window, and their ViewModels now implement the shared
`Mainguard.UI.ViewModels.ISettingsPage` — `OnActivated`/`OnDeactivated` — so `SettingsViewModel` can
lazily build, activate, and (for `DaemonLogsViewModel`, which is also `IDisposable`)
discard-and-rebuild each page's cached content on navigation), the Pro converters
(`AgentStatusBrushConverter`, `DiffLineKindToClassConverter`), the daemon-facing `Services/`
(`DaemonClient`/`DaemonBackedOrchestrator`/`ITerminalGateway`/`ICliAgentHost`/`IEgressAllowlistGateway`),
and the Pro manifest (`ProManifest`/`ProToolsSurface`). The SAME step moved the shared
**edition-composition seams** — `IEditionManifest` (+ its descriptors/enums),
`IAgentPlatformSurface`, `IProToolsSurface`, `ViewModelBase`, `ViewLocator`, plus the new
`IShellRailHost` (the narrow shell contract the Pro rail binds up to) — DOWN into `Mainguard.UI`, so
both the shell and the Pro UI depend on them without a cycle. The moved types were normalized to
`Mainguard.UI.*` CLR namespaces in the 2g sweep (the assembly split landed earlier, in 2e). Because
those kept namespaces make a namespace-keyed check unable to tell the assemblies apart, the
Pro↛shell and base↛upper-layer boundaries are pinned by `EditionReferenceGraphTests` via
`Assembly.GetReferencedAssemblies()` (assembly-identity), while the daemon-client / gRPC positives
stay namespace-keyed. The shell reaches the Pro Views only through `App.Edition`/`ViewLocator`
(`ProManifest.ViewAssemblies` = the Pro assembly; `App.ComposeViewAssemblies` prepends the shell),
and injects the App capabilities the Pro UI still needs — settings accessor, `oobe.log` sink,
shell-toast, sandbox-image rebuild engine, Add-Repos VM factory, shell-window factory, the host rail
descriptors, and the orchestrator-services factory — DOWN through `Editions/ProComposition`, wired
once in `App.WireProComposition` and `EditionManifests`' static ctor (the same "shell injects into
the base layer" inversion `ThemeManager.PersistKey` already uses).
`App.OrchestratorServicesFactory`/`CreateProductionOrchestratorServices` stay as thin forwarding
seams over `ProComposition` so the render harnesses' mock-injection is unchanged. The app icon
`Assets/avalonia-logo.ico` is duplicated into `Mainguard.Agents.UI/Assets/` (the moved
Startup/Shutdown/OOBE windows reference it by root-relative `/Assets/…`).

- **`Themes/`** — one `ResourceDictionary` per color theme (`MidnightLoom` default, `DaylightLoom`,
  `Graphite` — the macOS-native neutral-graphite dark with Apple-semantic-derived colors —
  `Atelier`), each defining the full token contract (incl. the P2-13 `AgentStatus*Brush`
  micro-badge tokens resolved by the App's `AgentStatusBrushConverter`). `CommandDeck` and
  `LoomAurora` were retired in the 4-theme restyle; `ThemeManager`'s `LegacyKeyMap` migrates their
  persisted keys (→ `Graphite` / `MidnightLoom`) and self-heals the store on first launch
  (pinned by `Headless/ThemeRetirementMigrationTests`).
  - `App.axaml` seeds `MidnightLoom` and `ThemeManager` swaps it at runtime via
    `avares://Mainguard.UI/Themes/{key}.axaml`. `ThemeManager.SystemKey` ("System") is a follow-
    the-OS MODE, never a `Themes` entry (the render harnesses sweep that list): it persists as its
    own key, resolves dark→Midnight Loom / light→Daylight Loom, and re-resolves live on the
    platform's `ColorValuesChanged`. Pinned by `Headless/SystemThemeModeTests`.
- **`Styles/Icons.axaml`** — the theme-INDEPENDENT resources: the `FontUi`/`FontMono` families +
  every icon `StreamGeometry` (window-control glyphs, rail/section icons, agent-lifecycle
  micro-badges, severity/signing glyphs). Deliberately NOT under `Themes/` — `ThemeManager.Apply`
  removes any merged dictionary whose source contains `/Themes/`, so this must survive that sweep.
  - `App.axaml` merges it into `Application.Resources` (for Views' `{StaticResource …}` /
    `{DynamicResource …}` icon lookups).
- **`Styles/DesignSystem.axaml`** — the component-class `<Styles>`
  (`Button.Primary/.Accent/.Success/.Danger/.DangerQuiet/.Secondary/.IconButton/.Pill/.Segment/.WindowButton`
  — `.DangerQuiet` is the UNFILLED destructive: `Button.Secondary`'s shape with `DangerBrush` text and
  hairline, for a destructive action that sits BESIDE a view's single accent CTA rather than being it
  (the merge-queue rail's per-row Discard, where the one accent is reserved for the Review CTA);
  `.Danger` stays the filled form, for the moment a destructive action IS the primary action — the
  confirmation step. Adds no tokens; both resolve `DangerBrush`/`DangerHover`, which every theme has —
  `Border.Card/.RefChip*/.SegmentTrack`, `ComboBox`, flyout/menu surfaces, `CheckBox`,
  `PathIcon.Chevron/.spinning`, typography, button hover fades).
  - `App.axaml` `StyleInclude`s it LAST (after `FluentTheme` + AvaloniaEdit + Dock) so these overrides
    win. Merges `Icons.axaml` into its own `Styles.Resources` so its four `{StaticResource}` icon/font
    lookups resolve inside the include scope.
- **`Theming/ThemeManager.cs`** — runtime theme switching: swaps the merged theme dictionary, sets
  the theme variant, raises `ThemeChanged`. Persists the chosen key through the `PersistKey`
  `Action<string>?` seam (the shell wires it to `App.Settings.Update(p => p.Theme = key)` in
  `App.OnFrameworkInitializationCompleted`) — the base layer never reaches up into `App.Settings`.
  Left null (headless harnesses, which always `Apply(…, persist: false)`) it's a no-op.
- **`Theming/VibrancyManager.cs`** — the opt-in macOS translucent-chrome switch: `Attach(mainWindow)`
  + `SetEnabled(bool)`. Sets the window's `TransparencyLevelHint` to AcrylicBlur and, only while the
  platform actually granted it (tracked via `ActualTransparencyLevel` — the grant is async and
  Reduce-Transparency can refuse), shadows the `ChromeWindowBackground`/`ChromePanelBackground`
  indirection tokens at app level with the active theme's `SurfaceWindowVibrant`/`SurfacePanelVibrant`
  variants (direct app-resource entries survive `ThemeManager`'s "/Themes/" sweep; re-resolves on
  `ThemeChanged`). Hard no-op off macOS/headless — the opaque defaults are the canonical
  harness-verified look. Driven by `UserPreferences.MacTranslucentChrome` from the MainWindow ctor
  and the Settings → General checkbox.
- **`Charts/ChartTheme.cs`** (T-22) — resolves LiveChartsCore paint colors from the theme tokens so every analytics chart follows the active theme instead of hardcoding hex (categorical graph-lane palette, Success/Danger churn pair, surface→Accent heat ramp). Consumed by the App's `AnalyticsViewModel`.
- **`Views/ChromedWindow.cs`** (#77) — the base `Window` every secondary dialog/panel derives from: extends the client area over the OS decorations (matching MainWindow) and exposes `BeginTitleBarDrag`/`ToggleMaximizeFromTitleBar`. Derived windows stay in `Mainguard.App.Shell/Views/` (same `Mainguard.App.Shell.Views` namespace, cross-assembly). Applies `WindowChromePolicy` after its own `NoChrome` hints.
- **`Platform/MacNative.cs`** — the one place managed code talks to AppKit directly (raw
  `objc_msgSend`, no binding package in the supply chain; every member is a safe no-op off macOS
  and never throws): `TryPostNotification` (Notification Center banner — real attribution only
  inside the .app bundle; reports false so callers keep their fallback) and `SetDockBadge` (the
  Dock icon's badge label; UI-thread-sensitive).
- **`Views/WindowChromePolicy.cs`** — the per-platform chrome policy for client-area-extended
  windows: Windows/Linux keep the hand-drawn `NoChrome` title bar; macOS overlays the system
  chrome instead (`NoChrome` would remove the traffic lights — the only close control there),
  hides the hand-drawn buttons (`CustomButtonsVisible`) and shifts title-bar content past the
  traffic-light cluster (`TitleBarPadding`). Consumed by `ChromedWindow`, `CustomTitleBar`, and
  MainWindow's code-behind.
- **`Controls/CustomTitleBar.axaml`(+`.cs`)** — the reusable hand-drawn title bar (drag/minimize/maximize/close) placed in row 0 of every `ChromedWindow`, reading `Title`/state off its ancestor window; on macOS its buttons hide and its padding insets per `WindowChromePolicy`.
- **`Converters/`** — the two git-free `IValueConverter`s: `BoolToOpacityConverter` and `ResourceKeyToGeometryConverter` (icon-key → `Icons.axaml`/theme `StreamGeometry`, the control-center badge lookup). Git-model converters (`AgentStatusBrushConverter`, `DiffLineKindToClassConverter`, `FileExtensionToIconConverter`) stayed in `Mainguard.App.Shell/Converters/`.
- **`Properties/XmlnsDefinitions.cs`** — assembly `XmlnsDefinition`s mapping `Mainguard.UI.Controls`/`.Views`/`.Converters` (+ the `Mainguard.UI` root) onto the standard Avalonia XML namespace, so the moved chrome/converters resolve prefix-free under the default `xmlns` in the shell/Pro-UI XAML (Avalonia's compiled `using:`/`clr-namespace:` otherwise searches only the compiling assembly).

## Role in the solution

- **`Mainguard.UI`** (step 2c) — the edition-agnostic **design-system base layer** both the shell
  and (later) the Pro UI render on: `Themes/*.axaml` (the theme dictionaries + full token
  contract), `Styles/Icons.axaml` (theme-independent fonts + icon `StreamGeometry`s) and
  `Styles/DesignSystem.axaml` (the `Button.*`/`Border.*`/ComboBox/… component-class styles) that
  `App.axaml` `ResourceInclude`s/`StyleInclude`s via `avares://Mainguard.UI/…`, `Theming/ThemeManager`
  (runtime theme swap; persistence via a `PersistKey` seam the shell wires), `Charts/ChartTheme`,
  generic chrome (`Views/ChromedWindow`, `Controls/CustomTitleBar`) and the git-free converters
  (`BoolToOpacityConverter`, `ResourceKeyToGeometryConverter`). **Avalonia-only (+ LiveChartsCore for
  ChartTheme); NO reference to Mainguard.Git / Mainguard.Agents / Mainguard.App.Shell / Docker / Grpc
  / Protos** (a clean base leaf — pinned by the reference-graph gate). Moved types were normalized to
  `Mainguard.UI.*` CLR namespaces in 2g (the assembly + `avares://` URIs changed in 2c); an assembly
  `XmlnsDefinition` exposes the moved chrome/converters on the default Avalonia xmlns so consuming
  XAML resolves them prefix-free. **Step 2e also moved the shared edition-composition seams DOWN
  here** — `Editions/IEditionManifest` (+
  `RailSectionDescriptor`/`SettingsPageDescriptor`/`EditionFirstRun`/`RailAdornmentKind`),
  `Editions/IAgentPlatformSurface`, `Editions/IProToolsSurface` (reshaped for the Settings-window
  rework: its methods used to each be `Task ManageXAsync(Window owner)` — open a dialog; the page
  factories now — `CreateAiProvidersPage`, `CreateAgentClisPage`, `CreateToolchainsPage` (the
  user-managed language-toolchain page, added with the toolchain channel), `CreateDaemonLogsPage`,
  `CreatePrIntakePage` (P2-12 external-PR intake — all daemon state, edited over gRPC; the page it
  builds had shipped as an orphaned `Window` nothing constructed, so intake was unconfigurable),
  `CreateMainguardOsPage(Window owner)` — just construct and return the page's content ViewModel as
  opaque `object`, the same `object?`-through-`ViewLocator` pattern
  `IAgentPlatformSurface.AgentRailContent`/`CreateResourceMonitor` already used, since
  `SettingsViewModel` drops them straight into a page slot instead of opening a dialog;
  `RebuildSandboxImagesAsync` is unchanged, since it never had a dialog either),
  `ViewModels/ViewModelBase`, `ViewLocator`, the narrow `ViewModels/IShellRailHost` (the shell
  contract the Pro rail binds up to), and `ViewModels/ISettingsPage` (a new, equally minimal
  `OnActivated()`/`OnDeactivated()` interface every Settings page's ViewModel implements, called by
  the Settings window's page-switch logic) — all normalized to `Mainguard.UI.*` namespaces in 2g so
  both the shell and the Pro UI reference them without a cycle; `CommunityToolkit.Mvvm` is now carried
  here (for `ViewModelBase`). Referenced by `Mainguard.App.Shell` and `Mainguard.Agents.UI`.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
