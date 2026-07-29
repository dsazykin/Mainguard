import { Link } from 'react-router';
import { Reveal } from '../lib/Reveal';
import { PatrolSpine } from '../components/PatrolSpine';
import {
  GraphVignette,
  StagingVignette,
  ConflictVignette,
  RefsVignette,
  ThemePanelVignette,
  WindowFrame,
} from '../components/vignettes';
import { IconGraph, IconStage, IconMerge, IconNoLock, IconWorktree, IconZap, IconGitHub } from '../components/Icons';

export function Client() {
  return (
    <div className="threaded">
      <PatrolSpine />
      <div className="container page-hero">
        <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
          Free forever · no account, ever
        </span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>The Git client, taken seriously again.</h1>
        <p className="lede">
          Most Git GUIs are a web page in a window: slow to open, slow to scroll, and account-walled.
          Mainguard is rendered natively — a precision instrument for the work you do a hundred times a
          day. Every window on this page is a working miniature, not a screenshot. Click around.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="What it fixes">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>What it fixes</h2>
          </Reveal>
          <Reveal delay={80}>
            <div className="ledger" role="img" aria-label="The problems with existing Git clients">
              <span className="err">✗</span> <span className="dim">Electron clients that take 200MB of RAM to show a diff</span>
              <br />
              <span className="err">✗</span> <span className="dim">"Free" tiers that demand a login and block private repos</span>
              <br />
              <span className="err">✗</span> <span className="dim">.git/index.lock corruption when two tools touch one repo</span>
              <br />
              <span className="err">✗</span> <span className="dim">Destructive operations one mis-click away from lost work</span>
              <br />
              <br />
              <span className="ok">✓</span> native rendering, instant startup, 60fps everywhere
              <br />
              <span className="ok">✓</span> the full client is free — no login, no telemetry, no held-hostage features
              <br />
              <span className="ok">✓</span> deterministic repository access: open, operate, release. No lock collisions.
              <br />
              <span className="ok">✓</span> the safe path is always the default path
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Features">
        <div className="container">
          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconGraph /> A commit graph that moves like the app is native — because it is
                </h3>
                <p className="muted">
                  Branches, merges and tags drawn as colored lanes at a rock-solid 60fps, whether
                  your history has a hundred commits or a hundred thousand. Scrolling never stutters;
                  selection never lags.
                </p>
              </div>
              <GraphVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconStage /> Stage exactly what you mean
                </h3>
                <p className="muted">
                  Files, hunks, or single lines — build precise commits from messy working
                  directories. The diff view shows whitespace ghosts and word-level changes so
                  nothing slips through. Try it: stage a few lines and commit them.
                </p>
              </div>
              <StagingVignette />
            </div>
          </Reveal>

          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconMerge /> Conflicts, without the cold sweat
                </h3>
                <p className="muted">
                  Ours, theirs, and the merged result side by side. Every choice is reversible until
                  you commit, and the UI always makes the safer path the obvious one — that's the
                  house rule.
                </p>
              </div>
              <ConflictVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconGitHub /> Your host, inside the client
                </h3>
                <p className="muted">
                  Pull requests, issues, reviews, CI checks, notifications and releases — first-class
                  panels, not browser tabs. Check out any PR into its own worktree with one click,
                  and jump from a blame line straight to the PR that introduced it.
                </p>
              </div>
              <WindowFrame title="mainguard — pull requests">
                <div className="vg-grid">
                  {[
                    ['#128 · feat: partial stash UI', 'checks ✓ · 2 approvals', 'var(--success)'],
                    ['#127 · fix: graph virtualization', 'checks running…', 'var(--warning)'],
                    ['#125 · docs: release notes', 'review requested — you', 'var(--accent)'],
                  ].map(([title, state, color]) => (
                    <div key={title} className="vg-row">
                      <span className="mono" style={{ fontSize: 12 }}>{title}</span>
                      <span className="mono" style={{ fontSize: 10.5, color, flexShrink: 0 }}>{state}</span>
                    </div>
                  ))}
                  <p className="vg-note">↳ checkout #128 into a new worktree · one click, working dir untouched</p>
                </div>
              </WindowFrame>
            </div>
          </Reveal>

          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconWorktree /> Branches, tags, stashes, worktrees
                </h3>
                <p className="muted">
                  Full management of everything your repository holds, in one window. Worktrees are
                  first-class — the same foundation that hosts agent sandboxes in Pro. Interactive
                  rebase, force-with-lease, LFS and stash apply/pop included, all behind safe
                  defaults.
                </p>
              </div>
              <RefsVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconNoLock /> A client that catches your mistakes first
                </h3>
                <p className="muted">
                  A pre-commit safety scanner flags secrets and keys before they enter history. The
                  commit composer nudges toward clean conventional commits. Destructive operations
                  ask once, clearly — and force-push is always force-with-lease.
                </p>
              </div>
              <WindowFrame title="mainguard — pre-commit scan">
                <div className="ledger" style={{ padding: '14px 16px', fontSize: '0.8rem', lineHeight: 1.9 }}>
                  <span className="dim">$ commit — scanning 3 staged files…</span>
                  <br />
                  <span className="err">⚠ appsettings.json:14 — looks like an API key (sk-…)</span>
                  <br />
                  <span className="dim">   [ unstage line ]  [ it's a fake, commit anyway ]</span>
                  <br />
                  <span className="ok">✓ history stays clean — caught before it ever left your machine</span>
                </div>
              </WindowFrame>
            </div>
          </Reveal>

          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconZap /> Five themes, one design system
                </h3>
                <p className="muted">
                  Midnight Watch, Day Watch, Command Deck, Atelier, Aurora. Every color in
                  the app is a named token — switch palettes live, and every surface follows. The
                  settings panel on the right is wired to this website; use it.
                </p>
              </div>
              <ThemePanelVignette />
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Everything else in the box">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>And the rest of the toolbox</h2>
            <p className="lede">The unglamorous features you reach for daily — done properly, included free.</p>
            <dl className="spec-list">
              {(
                [
                  ['Word-level diffs', 'whitespace ghosts, intra-line highlights, image diffs'],
                  ['Interactive rebase', 'reorder, squash, reword — with a live preview of the result'],
                  ['Undo, everywhere', 'reflog-backed undo for the operations that usually mean panic'],
                  ['Blame → PR jump', 'from any line to the pull request that introduced it'],
                  ['Conventional commits', 'a composer that formats type(scope): summary for you'],
                  ['Stash management', 'create, inspect, apply, pop — with diffs before you decide'],
                  ['Tags & releases', 'annotated tags, release notes, host releases in one panel'],
                  ['Submodules & LFS', 'status, init, update — no CLI round-trips'],
                  ['Bisect', 'guided good/bad bisection with build shortcuts (rolling out)'],
                  ['Global search', 'find code across all history, not just HEAD (rolling out)'],
                ] as Array<[string, string]>
              ).map(([t, d]) => (
                <div key={t}>
                  <dt>{t}</dt>
                  <dd>{d}</dd>
                </div>
              ))}
            </dl>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Why it is free">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Why free? Honestly:</h2>
            <p className="lede">
              The client is our handshake. It's complete — nothing is held hostage behind an
              upgrade prompt, and it never asks who you are. When you're ready to put coding
              agents to work, <Link to="/pro">Pro</Link> is where Mainguard earns its keep. Until
              then, enjoy a Git client that respects you.
            </p>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>Shipping this fall.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Join the waitlist and get the download link the moment it's live.
            </p>
            <Link to="/waitlist?p=client" className="btn btn-accent btn-lg">
              Get the client first
            </Link>
          </Reveal>
        </div>
      </section>
    </div>
  );
}
