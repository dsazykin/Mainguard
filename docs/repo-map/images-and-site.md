<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `images/` (P2-07 agent container images — built in CI/release, NEVER at runtime, G-16)

- **`images/mainguard-agent-base/`** — the static hardened agent base image: `Dockerfile` (**MG-27:
  `FROM debian:bookworm-slim@sha256:…` — pinned by digest, not by the floating tag — and every
  artefact fetched from outside the Debian archive is version-addressed and `sha256sum -c`-verified:
  the Determinate `nix-installer` binary at a pinned tag (the `curl … | sh` bootstrap script is
  skipped entirely) and the devbox binary from its pinned GitHub release (replacing the old
  launcher-then-`find`-the-cache recipe; verified byte-identical). Both `ARG` pairs are
  `<TOOL>_VERSION` + `<TOOL>_SHA256` — bump together.**) (Debian + git/curl + Nix; a **pre-baked,
  A6-clean curated toolchain** — jq/ripgrep/fd/tree/gnumake/nodejs/python3/go Nix-installed at BUILD
  time into a persistent `/opt/toolchain` profile that is on PATH from the read-only image, so it
  needs **zero runtime egress** and no git host, because devbox's runtime `add` resolves nixpkgs from
  github which A6 forbids; the real devbox binary is also baked for the deferred v1.x arbitrary-`add`
  path; two distinct non-root users `agent` uid 1000 / `supervisor` uid 1001 for G2 key custody;
  **`/opt/mainguard/adapters/bin` leads PATH** — the read-only mount of the dynamically-installed
  agent CLIs (`AdapterPaths.SandboxMount`), so a CLI installed AFTER this image was built is runnable
  by name in the jail with no image rebuild; the daemon itself launches by the marker's ABSOLUTE argv,
  so this is for the agent's own shell, and an absent mount is just an empty PATH entry. **Caveat:** a
  LOGIN shell (`bash -l`) re-derives PATH from `/etc/profile` and drops both this and `/opt/toolchain`
  — pre-existing, affects the Nix toolchain equally), `seccomp.json` (the default-deny profile itself
  — the canonical moby default with the three memory-inspection syscalls denied; **embedded into
  `Mainguard.Agents` and returned verbatim by `SeccompProfile.Json`** — one source of truth, what the
  tests assert equals what the container runs), `README.md`. **v1 provisioning:** this source tree
  ships with the app (`$(MainguardImageSources)` → `payload/images/mainguard-agent-base/`) and is
  docker-built inside MainguardEnv at startup when the VM's image store lacks it (Core
  `SandboxImageProvisioner`); the spawn preflight names it when still absent.
- **`images/mainguard-egress-proxy/`** — the default-deny egress proxy image: `Dockerfile`
  (tinyproxy + dnsmasq + iptables; **MG-27: `FROM debian:bookworm-slim@sha256:…`, the same pinned base
  digest as the agent image — refresh both together**), `entrypoint.sh` (waits for the daemon config
  push, then runs `reload.sh --boot`), `reload.sh` (applies the daemon-rendered tinyproxy allow-filter
  + **tinyproxy upstreams** + pinned-DNS + iptables backstop from `/run/mainguard/`; readiness means
  LISTENING, read out of `/proc/net/{tcp,udp}`, not merely "the process exists". **MG-41 — the daemon
  restart is an ~80 ms/~20 ms outage, so it happens only when it changes something:** a push skips it
  when the rendered daemon config is byte-identical to `applied.digest` (recorded only after both
  daemons were confirmed listening) AND both are still listening; `--boot` yields to any reload that
  has run or is running, so the entrypoint can never restart the proxy after `EnsureReadyAsync`
  returned. Both gates fail towards restarting, and a policy CHANGE always restarts. SIGHUP was
  rejected on measurement, not on principle: tinyproxy 1.11.1 does re-read its filter, but **dnsmasq
  2.90 does not re-read its config file** — a name removed from the allowlist and SIGHUP'd still
  answered its old address, while a restart answered `0.0.0.0` — so it would reintroduce the CAP_KILL
  bug more quietly. The P2-08 upstreams are **appended into the generated `tinyproxy.conf`** rather
  than `Include`d — tinyproxy 1.11 has no `Include` directive and refuses to start on one — which is
  the load step that was missing: the artefact was rendered on every push and read by nothing, so
  gateway fronting was inert), `README.md`. Config is rendered by
  `EgressProxyConfig`/`EgressProxyConfigurator`. **v1 provisioning:** ships with the app
  (`payload/images/mainguard-egress-proxy/`) and is docker-built in-VM at startup when missing; the
  spawn preflight makes its absence actionable (previously an opaque failure inside the egress setup).

### `docs/security-architecture.md` (P2-07-seeded, P2-17-owned)

The living security-architecture doc: the layered exfiltration controls (S-1 quarantine, G-15
hardened spec, G-11 ext4-only, default-deny egress, A6 no-direct-git-egress, G2
anti-memory-inspection quartet) and — stated honestly — the **F5 accepted-and-stated residual**
(public-payload pull + low-bandwidth request-path exfil via an allowlisted registry, bounded by
no-push/no-creds + the verify→review→human-merge backstop). Deliberately top-level (not a `docs/`
subfolder) because it is a cross-cutting security reference P2-17 expands.

### `site/` (marketing website — React/Vite, NOT part of the .NET solution)

The public **Mainguard** site at <https://mainguard.dev/> (GitHub Pages custom domain; the old
<https://dsazykin.github.io/Mainguard/> redirects once the domain is active). React 19 + Vite +
TypeScript SPA re-implementing the app's five-theme token design system in CSS; deployed by
`deploy-site.yml`. Forms are backed by a Cloudflare Worker (deployed separately from `site/worker/`,
**not** by CI). Commands (from `site/`): `npm run dev` / `npm run build` / `npm run lint`.
Credentials for the Worker live in the repo-root `.cloudflare.env` (git-excluded — never commit it).
The product rename is phased — `docs/rebrand/Mainguard_Rebrand_Plan.md` is the plan of record; the
repo is now `dsazykin/Mainguard` and tracked refs point at it, while some deployed identifiers (e.g.
the Cloudflare Worker name/URL) still carry the old name until their phase.

- **`index.html`** — meta/OG tags + pre-paint theme restore (reads `mainguard-theme`, falls back to the legacy `mainguard-theme`); **`vite.config.ts`** — `base: '/'` (custom domain serves at the root); **`public/`** — `favicon.svg`, `og.png`, `CNAME` (the Pages custom domain).
- **`src/config.ts`** — deployed Worker URL, Turnstile sitekey, GitHub URL. **`src/main.tsx`** / **`src/App.tsx`** — entry, router, per-route titles.
- **`src/styles/`** — `tokens.css` (the five palettes ported 1:1 from `Themes/*.axaml` as CSS custom
  properties on `data-theme`), `base.css` (global + shared component classes: `.btn*`, `.pill`,
  `.window*`, `.field`, reveals incl. `.from-left`/`.from-right` directional variants), `site.css`
  (nav/footer/hero/section patterns, `.thread-spine`, `.spec-list`, entrance animations),
  `vignettes.css` (shared interactive-vignette classes, `vg-*`).
- **`src/theme/`** — `themes.ts` (theme metadata), `ThemeProvider.tsx` (context + localStorage persistence).
- **`src/components/`** — `Nav.tsx` (mobile sheet is portalled to `<body>`: the nav's
  `backdrop-filter` would otherwise become the fixed sheet's containing block), `Footer.tsx`,
  `Wordmark.tsx` (the M-gatehouse mark), `ThemeSwitcher.tsx`, `GateHero.tsx` (the animated canvas:
  agent lanes stream IN from the left — the pattern drifts rightward, work arriving — straighten out
  before the prominent walled gate at ~62% width, and emerge as one guarded main line; verified
  changes flash as they clear the gate; honors reduced motion, pauses offscreen), `PatrolSpine.tsx`
  (the sentry patrol: one scroll-walked accent line down each page's left gutter, extended to the
  footer, its head a traveling watch-light; checkpoints at `[data-thread-node]` anchors clear — fill
  Success with a check — once the patrol passes; needs a `.threaded` relative parent),
  `SuccessGate.tsx` (shared form-success animation: lanes assemble a verified seal), `Icons.tsx`
  (inline SVG icon set), `Turnstile.tsx` (Cloudflare Turnstile wrapper).
- **`src/components/vignettes/`** — interactive CSS/SVG product miniatures (each pauses offscreen
  via IO and respects reduced motion): `WindowFrame.tsx` (shared chrome), `Graph.tsx` (clickable
  commits), `Staging.tsx` (click-to-stage + commit), `Conflict.tsx` (ours/theirs composer), `Refs.tsx`
  (checkout on click), `ThemePanel.tsx` (wired to the real site theme), `Agents.tsx` (live progress +
  expandable logs), `Pipeline.tsx` (sequential gates + replay), `ReviewQueue.tsx` (approve toggles),
  `Gateway.tsx` (ticking rates), `Cloud.tsx` (typing-prompt loop), `Radar.tsx` (conflict radar),
  `Intake.tsx` (external PR intake), `Audit.tsx` (hash-chain ledger), `index.ts` (re-exports).
- **`src/pages/`** — `Home.tsx`, `Client.tsx` (free client), `Pro.tsx`, `Cloud.tsx` (cloud product; `/weave` redirects to `/cloud`), `Contact.tsx` (one-question-at-a-time wizard with Next/Back, per-step validation, input preserved on failure), `Waitlist.tsx` (both POST to the Worker and share `SuccessGate`; Cloud's wire id stays `weave` until the Worker redeploy), `NotFound.tsx`.
- **`src/lib/`** — `Reveal.tsx` (enhance-only scroll reveals), `hooks.ts` (`useInView` / `useReducedMotion` / `useTicker` for vignette animation), `api.ts` (fetch helper).
- **`worker/`** — the form backend (Cloudflare Worker `mainguard-site-api`; the pre-rename
  `mainguard-site-api` stays deployed until traffic drains, then is deleted): `src/index.ts` (POST
  `/api/waitlist` + `/api/contact` with validation, Turnstile verify, honeypot, per-IP rate limiting,
  D1 storage, optional Resend email notify; GET `/api/admin/submissions` behind `ADMIN_TOKEN`),
  `schema.sql` (D1 schema), `wrangler.jsonc` (bindings; secrets documented inline). Deploy with
  `npx wrangler deploy` using the env vars from `.cloudflare.env`.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
