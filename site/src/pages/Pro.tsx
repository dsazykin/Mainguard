import { Link } from 'react-router';
import { Reveal } from '../lib/Reveal';
import { PatrolSpine } from '../components/PatrolSpine';
import {
  AgentsVignette,
  PipelineVignette,
  ReviewQueueVignette,
  IntakeVignette,
  GatewayVignette,
  AuditVignette,
  WindowFrame,
} from '../components/vignettes';
import { IconShield, IconEye, IconKey, IconWorktree, IconMerge, IconGraph, IconLock } from '../components/Icons';

export function Pro() {
  return (
    <div className="threaded">
      <PatrolSpine />
      <div className="container page-hero">
        <span className="pill pill-accent">Paid · waitlist open</span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>Where agent work becomes trustworthy commits.</h1>
        <p className="lede">
          Generating code is cheap now. The expensive part is what comes after: checking it,
          trusting it, merging it. Mainguard Pro runs several coding agents against one repository,
          each jailed on its own branch, and lets nothing reach main until it has been verified
          against current main and approved by you. Every window below is live — click, approve,
          replay.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="The problem">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>The bottleneck moved.</h2>
            <p className="lede">
              Agent CLIs made it trivial to produce ten branches an hour. The pipeline that decides
              which of them are safe to merge is still you, alt-tabbing between terminals, hoping
              nothing collides.
            </p>
          </Reveal>
          <Reveal delay={80}>
            <div className="ledger" role="img" aria-label="What goes wrong running multiple agents against one repository">
              <span className="dim">Three agents, one repo, no Mainguard:</span>
              <br />
              <span className="err">✗ agent-2 committed over agent-1's half-finished refactor</span>
              <br />
              <span className="err">✗ agent-3 force-pushed and ate your local fixes</span>
              <br />
              <span className="err">✗ 4,000 generated lines merged — tests never ran</span>
              <br />
              <br />
              <span className="dim">Three agents, one repo, Mainguard Pro:</span>
              <br />
              <span className="ok">✓ each agent works a jailed worktree — collisions are impossible</span>
              <br />
              <span className="ok">✓ every change passes build + tests + lint before it can merge</span>
              <br />
              <span className="ok">✓ you review verified diffs from one cockpit, then land them</span>
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
                  <IconWorktree /> Jailed parallel agents
                </h3>
                <p className="muted">
                  Five pinned CLI adapters — Claude Code, Codex, Gemini CLI, Qwen Code, OpenCode —
                  each running in its own hardened container: no-new-privileges, seccomp, dropped
                  capabilities, read-only rootfs, user namespaces and per-jail resource limits.
                  They can't touch each other's work and they can never touch your working
                  directory. Windows via a lightweight background WSL2 VM — no Docker Desktop
                  dependency — and native on macOS.
                </p>
              </div>
              <AgentsVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconShield /> A pass an agent cannot fake
                </h3>
                <p className="muted">
                  Nothing merges on vibes. The verdict is the container's real exit code, read by
                  the trusted daemon from outside the container — never a value the agent supplies.
                  The resolved test command and config hash are pinned immutably, so nothing slips
                  through by quietly weakening what "verify" means. If main moves, every other
                  verified branch is invalidated and re-verified before it is eligible again.
                </p>
              </div>
              <PipelineVignette />
            </div>
          </Reveal>

          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconEye /> The review cockpit
                </h3>
                <p className="muted">
                  All agent output flows into one queue of verified diffs, ranked by risk so you
                  spend attention where it matters. Every hunk carries provenance — which agent,
                  which run. Changes flagged as sensitive need an explicit acknowledgement before
                  you can approve them, and the merge itself is an atomic compare-and-swap, so two
                  racing agents cannot land a stale result. Verification earns a branch the right
                  to be considered; it never merges anything on its own.
                </p>
              </div>
              <ReviewQueueVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconMerge /> You approve the plan before anyone writes code
                </h3>
                <p className="muted">
                  Point Pro at a piece of work and a coordinator decomposes it into a plan — then
                  stops. No worker spawns until you have read that plan and let it through. The
                  coordinator's own tool surface is locked to a small contract that has nothing in
                  common with a worker's, so the thing that plans the work cannot quietly start
                  doing it.
                </p>
              </div>
              <WindowFrame title="mainguard pro — plan gate">
                <div className="vg-grid">
                  {[
                    ['1 · extract IImportSource from CsvImporter', 'Mainguard.Git/Import/'],
                    ['2 · add XlsxImportSource + tests', 'Mainguard.Git/Import/'],
                    ['3 · wire the wizard to the new source', 'Mainguard.App.Shell/'],
                  ].map(([step, scope]) => (
                    <div key={step} className="vg-row" style={{ display: 'block' }}>
                      <span className="mono" style={{ fontSize: 12, display: 'block' }}>{step}</span>
                      <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>{scope}</span>
                    </div>
                  ))}
                  <div className="vg-row" style={{ borderColor: 'var(--accent)' }}>
                    <span className="mono" style={{ fontSize: 11.5, color: 'var(--accent)' }}>
                      3 workers held — awaiting your approval
                    </span>
                  </div>
                  <p className="vg-note">nothing spawns until the plan clears the gate</p>
                </div>
              </WindowFrame>
            </div>
          </Reveal>

          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconGraph /> Vendor-neutral by design
                </h3>
                <p className="muted">
                  Agents that live elsewhere — Codex, Jules, Copilot — open PRs against your repo
                  all day. Pro pulls them into the same verify → review → merge pipeline as your
                  local agents, so there's one standard for what lands, no matter who or what wrote
                  it. Swap agents as models leapfrog each other; keep the workflow.
                </p>
              </div>
              <IntakeVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconKey /> Your keys, your models, your terms
                </h3>
                <p className="muted">
                  Bring your own API keys or existing agent subscriptions — they live in the OS
                  keyring, and Pro orchestrates without metering your tokens. The built-in gateway
                  smooths rate limits across the fleet so a burst from one agent doesn't starve the
                  rest, enforces per-agent and per-day token and cost budgets, applies honest
                  admission control instead of overselling your hardware, and tells you what every
                  merged change actually cost.
                </p>
              </div>
              <GatewayVignette />
            </div>
          </Reveal>

          <Reveal className="from-left">
            <div className="feature-row">
              <div>
                <h3 data-thread-node>
                  <IconLock /> An audit trail you can prove
                </h3>
                <p className="muted">
                  Every agent action, verification run, approval and merge lands in a hash-chained,
                  tamper-evident log with typed events, retention policy and RFC-3161 timestamp
                  anchoring — plus a command-line integrity check. When compliance asks "who
                  approved this AI-written change?", you answer in seconds. Built for the EU AI Act
                  era, useful long before an auditor shows up.
                </p>
              </div>
              <AuditVignette />
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="The stop control">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>One control stops everything.</h2>
            <p className="lede">
              An always-visible stop control freezes the merge queue first, then pauses every
              agent — in that order, so nothing slips through the gate on the way down. It runs
              under a hard thirty-second ceiling that a misbehaving worker cannot stretch. Wherever
              you can lose work, the safer path is the default one.
            </p>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="The rest of the arsenal">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>The rest of the arsenal</h2>
            <p className="lede">Everything else in Pro, each one built to keep agents honest.</p>
            <dl className="spec-list">
              {(
                [
                  ['Default-deny egress', 'model APIs and registries reachable from a jail; your git host is not'],
                  ['Read-only git proxy', 'only the daemon may reach a host — an agent cannot clone or exfiltrate'],
                  ['Pre-baked toolchains', 'declared and built ahead of time, so nothing fetches at runtime'],
                  ['Conflict parking', 'a branch that can no longer merge cleanly is parked, not silently dropped'],
                  ['Real terminals', 'genuine OS pseudo-terminals (ConPTY, forkpty) — not a pipe pretending'],
                  ['Persistent conversations', "an agent's CLI history survives its jail being rebuilt"],
                  ['Resource monitor', 'live CPU, memory and jail pressure across the fleet'],
                  ['External PR intake', 'bot-authored PRs enter the same gate as everything else'],
                  ['Per-jail limits', 'CPU, memory and lifetime ceilings you set, enforced by the daemon'],
                  ['Daemon logs', 'the privileged side is inspectable, not a black box'],
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

      <section className="section" aria-label="On the roadmap">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Honestly, not yet</h2>
            <p className="lede">
              Named because a roadmap is not a feature list. These are designed and intended, and
              none of them is in the product today.
            </p>
            <dl className="spec-list">
              {(
                [
                  ['Conflict radar', 'predicting collisions between live worktrees before merge time'],
                  ['SIEM streaming', 'pushing the audit log to an external security pipeline'],
                  ['AI reviewer pass', 'an optional model opinion alongside the deterministic gates'],
                  ['Vibe Mode', 'the plain-language build surface, as a finished experience'],
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

      <section className="section" aria-label="How Pro compares">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>Why not just use…</h2>
            <div style={{ display: 'grid', gap: 'var(--space-4)', maxWidth: '46rem', marginTop: 'var(--space-8)' }}>
              <p>
                <strong>…a vendor's own agent app?</strong>{' '}
                <span className="muted">
                  They orchestrate one vendor's agent. Pro is neutral ground — mix agents, swap
                  them as models leapfrog each other, keep one workflow and one audit trail.
                </span>
              </p>
              <p>
                <strong>…an orchestrator GUI?</strong>{' '}
                <span className="muted">
                  Most launch agents and hope. Almost none read the verdict from outside the
                  container, re-verify against a moved main, or treat your working directory as
                  sacred. Verification is the product here, not an afterthought.
                </span>
              </p>
              <p>
                <strong>…a cloud verification service?</strong>{' '}
                <span className="muted">
                  Your code stays on your machine, your keys stay yours, and the pipeline runs
                  offline. No per-PR metering, no phoning home, no waiting on someone else's queue.
                </span>
              </p>
              <p>
                <strong>…the terminal, like today?</strong>{' '}
                <span className="muted">
                  You can babysit two agents in tmux. You can't babysit five. The ceiling on your
                  leverage is the pipeline, and Pro is that pipeline.
                </span>
              </p>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>Pro is in alpha assembly.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              The pieces are built and tested; they are being wired into one control center.
              Pricing is announced at launch, and waitlist members get early access and founding
              terms.
            </p>
            <Link to="/waitlist?p=pro" className="btn btn-accent btn-lg">
              Join the Pro waitlist
            </Link>
          </Reveal>
        </div>
      </section>
    </div>
  );
}
