import { Link } from 'react-router';
import { Reveal } from '../lib/Reveal';
import { PatrolSpine } from '../components/PatrolSpine';
import { CloudVignette, WindowFrame } from '../components/vignettes';
import { IconCloud, IconShield, IconThreads, IconArrowRight } from '../components/Icons';

export function Cloud() {
  return (
    <div className="threaded">
      <PatrolSpine />
      <div className="container page-hero">
        <span className="pill pill-accent">Paid · cloud · waitlist open</span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>Describe it. Ship it verified.</h1>
        <p className="lede">
          Mainguard Cloud is for builders who don't want to think about git at all. Tell it what
          you want; coding agents build it in the cloud, every change passes the gate before it
          counts, and you get a working product — plus a clean, professional history underneath,
          in case you ever need it.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="The problem">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Vibe coding has a trust problem.</h2>
            <p className="lede">
              AI builders will happily generate an app for you. But when something breaks — and
              something always breaks — you're left holding ten thousand lines nobody checked, with
              no record of what changed or why. Cloud keeps the magic and posts a guard on the
              result.
            </p>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="How it works">
        <div className="container">
          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h2 data-thread-node style={{ marginBottom: 'var(--space-8)' }}>How it works</h2>
                <div className="steps">
                  <div className="step">
                    <div>
                      <h3>Say what you want</h3>
                      <p>
                        In plain language. A booking page, an internal tool, a storefront — no
                        setup, no repositories, no jargon.
                      </p>
                    </div>
                  </div>
                  <div className="step">
                    <div>
                      <h3>Agents build it in the cloud</h3>
                      <p>
                        Several agents work in parallel in isolated cloud sandboxes, each on its own
                        lane of the work. Nothing runs on your machine.
                      </p>
                    </div>
                  </div>
                  <div className="step">
                    <div>
                      <h3>Only verified work reaches you</h3>
                      <p>
                        Every change passes automated checks before it's allowed through the gate
                        and into your product. You preview the result, not the chaos behind it.
                      </p>
                    </div>
                  </div>
                </div>
              </div>
              <CloudVignette />
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Iterating">
        <div className="container">
          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3 data-thread-node>Iterate the way you talk</h3>
                <p className="muted">
                  There is no "edit code" step. Ask for the change the way you'd ask a contractor —
                  and like a good contractor, Cloud shows you the result, not the rubble. Every
                  request becomes its own change: checked at the gate, landed, and reversible.
                  Don't like it? Say so, and it's rolled back.
                </p>
              </div>
              <WindowFrame title="mainguard cloud — request: pottery studio">
                <div className="vg-grid">
                  <div className="vg-row" style={{ justifyContent: 'flex-start' }}>
                    <span style={{ fontSize: 13.5 }}>
                      "Make the deposit 20% and add a cancellation policy page."
                    </span>
                  </div>
                  <div className="vg-row" style={{ display: 'block', borderColor: 'var(--accent)' }}>
                    <span className="mono" style={{ fontSize: 11.5, color: 'var(--success)', display: 'block' }}>
                      ✓ landed — 2 changes · checks passed · preview updated
                    </span>
                    <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)', display: 'block', marginTop: 4 }}>
                      undo available · this request can be rolled back any time
                    </span>
                  </div>
                  <p className="vg-note">every ask = one verified, reversible change</p>
                </div>
              </WindowFrame>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="What you get">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>What you keep</h2>
            <div style={{ display: 'grid', gap: 'var(--space-6)', maxWidth: '46rem', marginTop: 'var(--space-8)' }}>
              <p>
                <IconCloud style={{ verticalAlign: '-4px', color: 'var(--accent)' }} />{' '}
                <strong>A live product, not a code dump.</strong>{' '}
                <span className="muted">
                  Cloud hosts what it builds — a real URL from day one. Iterate by asking; ship by
                  clicking.
                </span>
              </p>
              <p>
                <IconShield style={{ verticalAlign: '-4px', color: 'var(--accent)' }} />{' '}
                <strong>Verified changes, always.</strong>{' '}
                <span className="muted">
                  The same deterministic gates that power Mainguard Pro run behind every request you
                  make — tests pass or it doesn't land. Breakage stops at the gate, not on your
                  customers.
                </span>
              </p>
              <p>
                <IconThreads style={{ verticalAlign: '-4px', color: 'var(--accent)' }} />{' '}
                <strong>A real history, for the day you need it.</strong>{' '}
                <span className="muted">
                  Everything is proper git underneath: clean commits, full provenance, every change
                  attributed. Hand it to a developer — or to Mainguard Pro — and nothing is a black
                  box.
                </span>
              </p>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="What people build">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>What gets built</h2>
            <div className="ledger" role="img" aria-label="Examples of things people build with Cloud">
              <span className="dim">requests that recently cleared the gate:</span>
              <br />
              <span className="ok">✓</span> a booking page with deposits and reminder emails — a pottery studio
              <br />
              <span className="ok">✓</span> an inventory tracker with barcode scans — a bike shop
              <br />
              <span className="ok">✓</span> a members portal with paid tiers — a climbing gym
              <br />
              <span className="ok">✓</span> an internal quoting tool replacing four spreadsheets — a joinery
              <br />
              <span className="dim">no repositories were harmed — or seen — in the making of any of them</span>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Who it is for">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Made for the founders, not the git logs.</h2>
            <p className="lede">
              Non-technical founders, designers, operators, tinkerers — anyone with something to
              build and no appetite for merge conflicts. And if you outgrow it, nothing is thrown
              away: your project graduates to <Link to="/pro">Pro</Link> with its full history
              intact, ready for the developer you hire. Nothing to migrate, nothing to untangle.
            </p>
            <p style={{ marginTop: 'var(--space-6)' }}>
              <Link to="/pro" style={{ display: 'inline-flex', alignItems: 'center', gap: 8, fontWeight: 600 }}>
                See what Pro adds when you're ready <IconArrowRight />
              </Link>
            </p>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>Cloud arrives after Pro.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Paid, cloud-based, pricing announced at launch. The waitlist gets first invites.
            </p>
            <Link to="/waitlist?p=cloud" className="btn btn-accent btn-lg">
              Join the Cloud waitlist
            </Link>
          </Reveal>
        </div>
      </section>
    </div>
  );
}
