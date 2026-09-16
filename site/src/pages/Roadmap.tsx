import { Link } from 'react-router';
import { ROADMAP_REVIEWED } from '../config';

/**
 * What exists, what is being wired together, and what is still an intention.
 *
 * Deliberately carries NO dates. A solo project cannot honour a public
 * timeline, and a missed date on a roadmap page does more damage than having
 * no date at all. Ordering within a group is not a commitment either.
 *
 * Categories are taken from PRODUCT.md, which is the source of truth for what
 * is built. When something moves between groups there, it moves here in the
 * same change, and ROADMAP_REVIEWED moves with it.
 *
 * Not exempt from the copy guard: no em dashes.
 */

type Item = [title: string, detail: string];

const SHIPPED: Item[] = [
  ['The Git client', 'commit graph, staging down to the line, diffs, the three-pane conflict editor, branches, tags, worktrees, four themes'],
  ['Hardened sandboxes', 'no-new-privileges, seccomp, dropped capabilities, read-only rootfs, user namespaces, per-jail limits and reaping'],
  ['Default-deny egress', 'model APIs and registries resolve from a jail, your git host does not. The daemon reaches a host through a read-only proxy'],
  ['The verified merge queue', 'daemon-observed verification, the stale-invalidation cascade, conflict parking, and an exactly-once atomic merge with no auto-merge path'],
  ['Five pinned CLI adapters', 'Claude Code, Codex, Gemini CLI, Qwen Code and OpenCode, each pinned by version and sha256'],
  ['The review cockpit', 'risk ranking, per-hunk provenance, and an acknowledgement gate on flagged changes'],
  ['The coordinator and its plan gate', 'worker-authored plans with a revision cap, and a coordinator tool surface disjoint from a worker\'s'],
  ['The stop control', 'freezes the queue first, then pauses every agent, under a ceiling a worker cannot stretch'],
  ['The AI gateway', 'keys in the OS keyring, per-agent and per-day token and cost budgets, rate-limit backoff and admission control'],
  ['External PR intake', 'bot-authored PRs enter the same verify, review and merge path as local agents'],
  ['The audit log', 'hash-chained and tamper-evident, with typed events, retention, timestamp anchoring and a verification command'],
  ['Real pseudo-terminals', 'ConPTY on Windows, forkpty on macOS'],
  ['MainguardOS, installer and uninstaller', 'the Windows bootstrapper and first-run flow'],
];

const BUILDING: Item[] = [
  [
    'End-to-end assembly',
    'the pieces above exist and are tested. Wiring them into one control center a developer can run start to finish is the live piece of work, and it is what stands between now and a public build',
  ],
  [
    'The production terminal engine',
    'a full grid engine exists behind a flag. The interim engine stays the default until parity is signed off',
  ],
  [
    'macOS first-run',
    'the daemon and sandboxes run natively. The guided first-run flow is partial, so launch currently goes straight to the control center',
  ],
  [
    'A measured stop time',
    'the stop control is bounded by design. The round-trip has not been measured, and no number for it will be published until it has been',
  ],
];

const PLANNED: Item[] = [
  ['Conflict radar', 'predicting collisions between live worktrees before merge time, rather than discovering them at the gate'],
  ['SIEM streaming', 'pushing the audit log into an external security pipeline'],
  ['An optional AI reviewer', 'a model opinion offered alongside the deterministic gates, never in place of them'],
  ['Vibe Mode', 'the plain-language build surface, as a finished experience rather than a surface with thin evidence behind it'],
  ['Mainguard Cloud', 'the hosted product described on its own page. It comes after Pro, and none of it is built'],
];

const NOT_PLANNED: Item[] = [
  [
    'A Linux desktop build',
    'Linux is the substrate the sandboxes run on, not a desktop product. There is no packaging lane for it and none is intended',
  ],
  [
    'Auto-merge',
    'verification earns a branch the right to be considered and a human decides. A feature that quietly merged agent work would contradict the product',
  ],
];

function Group({
  status,
  tone,
  title,
  blurb,
  items,
}: {
  status: string;
  tone: string;
  title: string;
  blurb: string;
  items: Item[];
}) {
  return (
    <section className="roadmap-group">
      <span className={`roadmap-status ${tone}`}>{status}</span>
      <h2>{title}</h2>
      <p>{blurb}</p>
      <dl className="spec-list">
        {items.map(([t, d]) => (
          <div key={t}>
            <dt>{t}</dt>
            <dd>{d}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

export function Roadmap() {
  return (
    <div className="container legal">
      <p className="legal-updated">Last reviewed · {ROADMAP_REVIEWED}</p>
      <h1>Roadmap</h1>
      <p className="lede">
        What exists today, what is being wired together, and what is still an intention. Anything
        on this site marked with a dagger is in one of the last two groups.
      </p>

      <div className="legal-callout">
        <p>
          <strong>There are no dates on this page, on purpose.</strong> Mainguard is built by one
          person, and a public timeline from a solo project is a promise that gets broken. A missed
          date would cost more than the date was ever worth. Order within a group is not a
          commitment either.
        </p>
      </div>

      <Group
        status="Built"
        tone="is-shipped"
        title="What exists"
        blurb="Implemented and covered by tests, including sandbox security tests that run against a real container engine in CI."
        items={SHIPPED}
      />

      <Group
        status="Being wired"
        tone="is-building"
        title="What is in flight"
        blurb="Real work in progress. Described here so nobody has to infer it from silence."
        items={BUILDING}
      />

      <Group
        status="Intended"
        tone=""
        title="What is planned"
        blurb="Designed and intended, and not in the product. Everything in this group carries the dagger wherever the site mentions it."
        items={PLANNED}
      />

      <Group
        status="Not planned"
        tone="is-no"
        title="What is not coming"
        blurb="Worth stating plainly, because an absent feature reads as an oversight unless someone says otherwise."
        items={NOT_PLANNED}
      />

      <section className="roadmap-group">
        <h2>How this page is kept honest</h2>
        <p>
          The groups above come from the same internal record that governs what the rest of the
          site may claim, and a feature moves here in the same change that moves it there. If you
          find something described elsewhere on this site as though it exists when it sits in the
          last two groups, that is a bug worth reporting through the{' '}
          <Link to="/contact">contact form</Link>.
        </p>
        <p>
          For how the verification claim actually works, including its limits, see{' '}
          <Link to="/verification">how verification works</Link>.
        </p>
      </section>
    </div>
  );
}
