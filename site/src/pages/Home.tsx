import { Link } from 'react-router';
import { GateHero } from '../components/GateHero';
import { PatrolSpine } from '../components/PatrolSpine';
import { ThemeSwitcher } from '../components/ThemeSwitcher';
import { Reveal } from '../lib/Reveal';
import { IconArrowRight } from '../components/Icons';
import { Planned, PlannedFootnote } from '../components/Planned';

export function Home() {
  return (
    <>
      <section className="hero" aria-label="Introduction">
        <GateHero />
        <div className="hero-scrim" aria-hidden />
        <div className="container">
          <div className="hero-content">
            <h1>Agents do the work. The guard holds main.</h1>
            <p className="lede">
              Agent CLIs made it trivial to produce ten branches an hour. Nothing on the market
              makes it safe to merge them. Mainguard is a native Git client, free and with no
              account, and a control center where agent work turns into commits you can trust.
            </p>
            <div className="hero-ctas">
              <Link to="/waitlist" className="btn btn-accent btn-lg" data-cta="waitlist-home-hero">
                Join the waitlist
              </Link>
              <Link to="/client" className="btn btn-quiet btn-lg" data-cta="explore-client-hero">
                Explore the client
              </Link>
            </div>
            <p className="hero-note">native · 60fps · zero telemetry · Windows and macOS</p>
          </div>
        </div>
      </section>

      <div className="threaded">
      <PatrolSpine />
      <section className="section" aria-label="Why Mainguard exists">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Born from one error message.</h2>
            <p className="lede">
              Run two tools against the same repository and sooner or later you meet it. Now
              multiply that by a room full of coding agents.
            </p>
          </Reveal>
          <Reveal delay={80}>
            <div className="ledger" role="img" aria-label="Terminal transcript showing the index.lock failure Mainguard prevents">
              <span className="dim">$ git commit -m "fix: parser edge case"</span>
              <br />
              <span className="err">fatal: Unable to create '.git/index.lock': File exists.</span>
              <br />
              <span className="err">Another git process seems to be running in this repository…</span>
              <br />
              <br />
              <span className="dim"># Mainguard opens the repository deterministically, does the work,</span>
              <br />
              <span className="dim"># and releases the handle. Every operation. Every time.</span>
              <br />
              <span className="ok">✓ committed 3 files · handle released in 41ms</span>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="How verification works">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>A pass an agent cannot fake.</h2>
            <p className="lede">
              Most tools ask the agent whether its work is good. Mainguard never does. The verdict
              is the container's real exit code, read by a trusted daemon from outside the
              container. Printing "all tests passed" buys an agent nothing.{' '}
              <Link to="/verification">See the whole mechanism</Link>, including what it does not
              do.
            </p>
          </Reveal>
          <Reveal delay={80}>
            <dl className="spec-list">
              {(
                [
                  [
                    'Observed by the daemon',
                    'the exit code is read from the container runtime, a value no agent gets to supply',
                  ],
                  [
                    'Checked against current main',
                    'when anything merges, every other verified branch is invalidated and re-verified before it can land',
                  ],
                  [
                    'Pinned to a command',
                    'the resolved test command and its config hash are recorded immutably, so nothing slips through by quietly weakening what verify means',
                  ],
                  [
                    'Never automatic',
                    'passing earns a branch the right to be considered. You still approve it, and the merge is an atomic compare-and-swap',
                  ],
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

      <section className="section" aria-label="Products">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Three ways to hold the line.</h2>
          </Reveal>
          <div className="trio">
            <Reveal>
              <Link to="/client" className="trio-row">
                <div>
                  <div className="trio-meta">
                    <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
                      Free forever
                    </span>
                    <span className="pill">No account. Ever.</span>
                  </div>
                  <h3>Git Client</h3>
                  <p>
                    A fast, native Git GUI with a 60fps commit graph, partial staging, calm
                    conflict resolution and real worktrees. None of the Electron weight.
                  </p>
                </div>
                <IconArrowRight className="trio-arrow" />
              </Link>
            </Reveal>
            <Reveal delay={90}>
              <Link to="/pro" className="trio-row">
                <div>
                  <div className="trio-meta">
                    <span className="pill pill-accent">Paid · waitlist open</span>
                  </div>
                  <h3>Mainguard Pro</h3>
                  <p>
                    Several coding agents, each jailed in its own hardened container on its own
                    branch. Nothing they write reaches main until your build and tests have passed
                    and you have waved it through.
                  </p>
                </div>
                <IconArrowRight className="trio-arrow" />
              </Link>
            </Reveal>
            <Reveal delay={180}>
              <Link to="/cloud" className="trio-row">
                <div>
                  <div className="trio-meta">
                    <span className="pill pill-accent">Planned · cloud · waitlist open</span>
                  </div>
                  <h3>
                    Mainguard Cloud
                    {/* The whole card is already a link, so this one must not be. */}
                    <Planned what="Mainguard Cloud" linked={false} />
                  </h3>
                  <p>
                    Describe what you want built. Agents build it in the cloud and every change is
                    verified before it lands. You get a working product with a clean history
                    underneath, and no git to learn.
                  </p>
                </div>
                <IconArrowRight className="trio-arrow" />
              </Link>
            </Reveal>
          </div>
        </div>
      </section>

      <section className="section" aria-label="Themes">
        <div className="container">
          <Reveal>
            <div className="theme-strip">
              <div style={{ maxWidth: '38rem' }}>
                <h2 data-thread-node style={{ marginBottom: 'var(--space-3)' }}>This page is wearing the app.</h2>
                <p className="muted" style={{ margin: 0 }}>
                  Mainguard ships one design system with four palettes: Midnight Loom, Daylight
                  Loom, Graphite and Atelier. Try them. Every surface on this page follows the
                  switch, the same way the client does.
                </p>
              </div>
              <ThemeSwitcher large />
            </div>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>The free client is in alpha.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Be first in line for the download, and for early access to Pro.
            </p>
            <Link to="/waitlist" className="btn btn-accent btn-lg" data-cta="waitlist-home-footer">
              Join the waitlist
            </Link>
          </Reveal>
        </div>
      </section>

      <div className="container">
        <PlannedFootnote />
      </div>
      </div>
    </>
  );
}
