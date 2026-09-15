# mainguard.dev

The Mainguard marketing site: a React 19 + TypeScript + Vite single-page app, deployed to GitHub
Pages and served at <https://mainguard.dev> (custom domain via `public/CNAME`).

## Branching — read this first

**`main` is the deployment branch for this site.** `.github/workflows/deploy-site.yml` builds and
publishes on every push to `main` that touches `site/**`. Product work happens on `phase2`, but
site changes must land on `main` or they never go live. When taking product truth from `phase2`,
port the *content*, not the branch.

## Commands

```bash
npm ci        # install (lockfile-exact)
npm run dev   # vite dev server with HMR
npm run build # tsc -b && vite build, then copy index.html to 404.html for SPA routing on Pages
npm run lint  # oxlint
npm run preview # serve the production build locally
```

`build` copies `dist/index.html` to `dist/404.html` because GitHub Pages has no SPA rewrite — the
404 page is what makes deep links like `/pro` work.

## Layout

```
src/
  pages/        one component per route (Home, Client, Pro, Cloud, Waitlist, Contact,
                Privacy, Terms, NotFound)
  components/   Nav, Footer, Wordmark, ThemeSwitcher, GateHero, PatrolSpine, Turnstile,
                SuccessGate, CookieConsent
    vignettes/  the interactive "working miniature" app windows embedded in the feature rows
  theme/        THEMES (the four palettes) + ThemeProvider
  styles/       tokens.css (design tokens), base.css, site.css, vignettes.css
  lib/          api client, hooks, Reveal (scroll-reveal wrapper), consent
worker/         Cloudflare Worker + D1 backend for the waitlist and contact forms
```

## Themes

The site wears the app's own design system: one system, **four** switchable palettes — Midnight
Loom (default), Daylight Loom (light), Graphite, Atelier. `src/styles/tokens.css` is ported 1:1
from `Mainguard.UI/Themes/*.axaml` on `phase2`, and `DESIGN.md` is the authority.

Two rules when touching themes:

- Every palette must define the **same** token set. A token missing from one palette silently
  falls back to the `:root` (Midnight) value, which is the failure mode this is easy to hit.
- Command Deck and Loom Aurora were retired in the 2026-08 restyle. Their ids are not reused, and
  both the `ThemeProvider` and the pre-paint script in `index.html` validate a stored id against
  the live four so an old preference falls through to the default.

`LEGACY_THEME_STORAGE_KEY` is deliberately still spelled `gitloom-theme`: it names a key already
sitting in pre-rename visitors' browsers, so renaming it would silently drop their choice.

## Content accuracy

`PRODUCT.md` on `phase2` is the source of truth for what may be claimed here, including an explicit
list of things that are **planned only and must not be described as existing** — and a standing
rule never to imply users, customers, pilots or testimonials, because there are none. The Pro page
keeps its roadmap items in a separate, clearly labelled "Honestly, not yet" section for this
reason. The GTM plan also forbids leading with "swarm" or "orchestration"; the claim is
safe-to-merge.

## Legal pages and consent

`/privacy` and `/terms` live in `src/pages/`, and both read the operator identity, governing law
and "last updated" date from `src/config.ts` — change them there, once, not in the prose.

**The privacy policy is a description of what the code does.** If you change what
`worker/src/index.ts` collects, what `worker/schema.sql` stores, or which third-party script the
site loads, update the policy in the *same commit*. Stale marketing copy is embarrassing; a stale
privacy policy is a legal problem.

Cookie consent (`src/lib/consent.tsx`, `src/lib/consentCategories.ts`, `CookieConsent.tsx`) is
built, wired and **deliberately dormant**. The site's only browser storage is the theme preference
the visitor sets and what Turnstile needs on the two form pages — both strictly necessary, both
exempt from consent, so no banner is shown. Showing one anyway would be misleading.

To turn it on when analytics or similar is added:

1. Add the category to `NON_ESSENTIAL` in `src/lib/consentCategories.ts`. That single edit enables
   the banner, the preferences dialog and the footer "Cookie settings" control.
2. Gate the new script on `useConsent().hasConsent('analytics')`, which returns false until the
   visitor opts in.
3. Add the cookie to the table in `src/pages/Privacy.tsx` §4.
4. Bump `CONSENT_VERSION` if the category set changed, so prior decisions are re-asked.

## Forms backend

`worker/` is a Cloudflare Worker with a D1 database, fronted by Turnstile and sending via Resend.
`src/config.ts` points the SPA at it (`API_BASE`). It deploys separately from this site via
Wrangler — a Pages deploy does not update the worker.
