import { Link } from 'react-router';
import { Reveal } from '../lib/Reveal';
import { PatrolSpine } from '../components/PatrolSpine';
import {
  AgentsVignette,
  PipelineVignette,
  ReviewQueueVignette,
  RadarVignette,
  IntakeVignette,
  GatewayVignette,
  AuditVignette,
} from '../components/vignettes';
import { IconShield, IconEye, IconKey, IconWorktree, IconMerge, IconGraph, IconLock } from '../components/Icons';

export function Pro() {
  return (
    <div className="threaded">
      <PatrolSpine />
      <div className="container page-hero">
        <span className="pill pill-accent">Paid · waitlist open</span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>Run a swarm. Keep control.</h1>
        <p className="lede">
          Coding agents are fast. Unsupervised, they're also reckless — they overwrite each other,
          break your working directory, and ship code nobody verified. Mainguard Pro turns one
          repository into a safe runway for many agents, with you in the tower. Every window below
          is live — click, approve, replay.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="The problem">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>The bottleneck moved.</h2>
            <p className="lede">
              Generating code is cheap now. The expensive part is what happens after: checking it,
              trusting it, merging it. Today that pipeline is you, alt-tabbing between terminals,
              praying nothing collides.
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
              <span className="ok">✓ each agent works a sandboxed worktree — collisions are impossible</span>
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
                  <IconWorktree /> Sandboxed parallel agents
                </h3>
                <p className="muted">
                  Launch Claude Code, AGY, OpenCode — any CLI agent — each in its own isolated
                  worktree inside a hardened sandbox with default-deny network egress. They can't
                  touch each other's work, they can't exfiltrate your code, and they can never touch
                  your working directory. Windows-first, WSL2-native — the platform everyone else
                  skipped.
                </p>
              </div>
              <AgentsVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconShield /> The merge queue is the product
                </h3>
                <p className="muted">
                  Nothing merges on vibes. Every agent branch enters a local merge queue: your
                  build, your tests, your linters — deterministic gates, not a model's opinion of
                  its own work. If main moves, stale verifications are invalidated and re-run
                  automatically. It works pre-PR, offline, on any repo — no CI round-trips, no
                  GitHub required.
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
                  which prompt, which run. Approve, request changes, or pull any branch into a
                  local worktree to feel it under your own hands.
                </p>
              </div>
              <ReviewQueueVignette />
            </div>
          </Reveal>

          <Reveal className="from-right">
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconMerge /> Conflict radar: collisions predicted, not discovered
                </h3>
                <p className="muted">
                  Pro watches every live worktree against every other — and against main — down to
                  the symbol level. When two agents drift toward the same function, you know minutes
                  in, not hours later at merge time. One click rebases the loser before anything
                  burns.
                </p>
              </div>
              <RadarVignette />
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
                  local swarm, so there's one standard for what lands, no matter who or what wrote
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
                  Bring your own API keys or existing agent subscriptions — Pro orchestrates, it
                  doesn't meter your tokens. The built-in gateway smooths rate limits across the
                  fleet so a burst from one agent doesn't starve the rest, applies honest admission
                  control instead of overselling your hardware, and tells you what every merged
                  change actually cost.
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
                  tamper-evident log — with a one-command integrity check and SIEM export. When
                  compliance asks "who approved this AI-written change?", you answer in seconds.
                  Built for the EU AI Act era, useful long before an auditor shows up.
                </p>
              </div>
              <AuditVignette />
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="The rest of the arsenal">
        <div className="container">
          <Reveal>
            <h2 data-thread-node>The rest of the arsenal</h2>
            <p className="lede">Everything else in Pro, each one built to keep a swarm honest.</p>
            <dl className="spec-list">
              {(
                [
                  ['Ticket → verified PR', 'point Pro at an issue; get a gated, reviewed branch back'],
                  ['Automations & schedules', 'recurring agent jobs whose output still passes every gate'],
                  ['Session board', 'every agent session on one kanban — compare candidates side by side'],
                  ['Checkpoints & forking', 'snapshot a session, rewind it, or fork it to try two approaches'],
                  ['Plan approval mode', "review the agent's plan before it writes a single line"],
                  ['Commit-stream curation', 'squash agent WIP noise into clean conventional commits'],
                  ['Inline comments → agent', 'annotate a diff; the agent addresses it in place'],
                  ['Multi-repo dashboard', 'one control surface across every repository you steer'],
                  ['Dev-server preview', "run and preview an agent's branch in-app, ports managed"],
                  ['Remote monitoring', 'watch the fleet from another machine — or your phone'],
                  ['CLI · SDK · MCP · API', 'the whole orchestrator is scriptable, not a walled garden'],
                  ['Agent flight recorder', 'full replay of what an agent saw, did, and why'],
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
                  Most launch agents and hope. Almost none verify output with deterministic gates,
                  predict conflicts between live worktrees, or treat your working directory as
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
            <h2>Pro launches after the free client.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Paid, with pricing announced at launch. Waitlist members get early access and founding
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
