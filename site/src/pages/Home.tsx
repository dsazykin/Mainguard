import { Link } from 'react-router';
import { GateHero } from '../components/GateHero';
import { PatrolSpine } from '../components/PatrolSpine';
import { ThemeSwitcher } from '../components/ThemeSwitcher';
import { Reveal } from '../lib/Reveal';
import { IconArrowRight } from '../components/Icons';

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
              Mainguard is a premium, natively-rendered Git client — free, no login — growing into
              a control center for running many coding agents against one repo, where nothing lands
              on main until it's verified and you wave it through.
            </p>
            <div className="hero-ctas">
              <Link to="/waitlist" className="btn btn-accent btn-lg">
                Join the waitlist
              </Link>
              <Link to="/client" className="btn btn-quiet btn-lg">
                Explore the client
              </Link>
            </div>
            <p className="hero-note">native · 60fps · zero telemetry · Windows first</p>
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
              multiply by a swarm of coding agents.
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
                    A fast, native Git GUI: 60fps commit graph, partial staging, calm conflict
                    resolution, worktrees — and none of the Electron heft.
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
                    Run a swarm of coding agents in sandboxed worktrees, gate every change through a
                    verification pipeline, and review it all from one cockpit.
                  </p>
                </div>
                <IconArrowRight className="trio-arrow" />
              </Link>
            </Reveal>
            <Reveal delay={180}>
              <Link to="/cloud" className="trio-row">
                <div>
                  <div className="trio-meta">
                    <span className="pill pill-accent">Paid · cloud · waitlist open</span>
                  </div>
                  <h3>Mainguard Cloud</h3>
                  <p>
                    Describe what you want built. Agents build it in the cloud, every change
                    verified before it lands — you get a working product and a clean history, no
                    git required.
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
                  Mainguard ships one design system with five switchable palettes — Midnight Watch,
                  Day Watch, Command Deck, Atelier and Aurora. Try them. Everything you're looking
                  at changes the guard, exactly like the client does.
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
            <h2>The free client ships this fall.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Be first in line for the download — and early access to Pro and Cloud.
            </p>
            <Link to="/waitlist" className="btn btn-accent btn-lg">
              Join the waitlist
            </Link>
          </Reveal>
        </div>
      </section>
      </div>
    </>
  );
}
