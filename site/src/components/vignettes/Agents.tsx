import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView, useReducedMotion, useTicker } from '../../lib/hooks';

interface AgentDef {
  name: string;
  rate: number;
  start: number;
  log: Array<[number, string]>; // [visible-from-pct, line]
}

const AGENTS: AgentDef[] = [
  {
    name: 'claude-code · fix/flaky-tests',
    rate: 1.4,
    start: 62,
    log: [
      [5, '> reading TestScheduler.cs'],
      [30, '> pinning clock in 3 flaky tests'],
      [62, '> dotnet test · 214/214 passed'],
      [96, '> gates green · awaiting your review'],
    ],
  },
  {
    name: 'codex · feat/import-wizard',
    rate: 0.9,
    start: 28,
    log: [
      [5, '> scaffolding ImportWizardViewModel'],
      [40, '> wiring CSV column mapping'],
      [70, '> dotnet build · 0 warnings'],
      [96, '> running test suite…'],
    ],
  },
  {
    name: 'opencode · refactor/db-layer',
    rate: 0.55,
    start: 6,
    log: [
      [5, '> mapping AppDbContext usages'],
      [35, '> extracting repository interfaces'],
      [70, '> migrating call sites (12/31)'],
      [96, '> …'],
    ],
  },
];

function stateFor(pct: number): { label: string; color: string } {
  if (pct >= 100) return { label: 'Verified', color: 'var(--success)' };
  if (pct >= 60) return { label: 'Running tests', color: 'var(--warning)' };
  return { label: 'Writing', color: 'var(--info)' };
}

/** Live agent fleet — progress ticks while visible; click an agent for its log. */
export function AgentsVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const reduced = useReducedMotion();
  const [pcts, setPcts] = useState(() => AGENTS.map((a) => a.start));
  const [openIdx, setOpenIdx] = useState<number | null>(0);
  const restRef = useRef(0);

  useTicker(
    () => {
      setPcts((prev) => {
        if (prev.every((p) => p >= 100)) {
          restRef.current += 1;
          if (restRef.current < 14) return prev; // hold the all-green frame ~2s
          restRef.current = 0;
          return AGENTS.map((a) => a.start * 0.4);
        }
        return prev.map((p, i) => Math.min(p + AGENTS[i].rate * (0.6 + Math.random() * 0.8), 100));
      });
    },
    150,
    inView && !reduced,
  );

  // Reduced motion: show a meaningful static frame instead of a frozen start.
  const shown = reduced ? [100, 64, 31] : pcts;

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard pro · agents">
        <div className="vg-grid">
          {AGENTS.map((a, i) => {
            const st = stateFor(shown[i]);
            const open = openIdx === i;
            return (
              <button
                key={a.name}
                type="button"
                className="vg-row"
                style={{ display: 'block' }}
                aria-expanded={open}
                onClick={() => setOpenIdx(open ? null : i)}
              >
                <span style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                  <span className="mono" style={{ fontSize: 12 }}>{a.name}</span>
                  <span className="pill" style={{ borderColor: st.color, color: st.color, background: 'transparent', flexShrink: 0 }}>
                    {st.label}
                  </span>
                </span>
                <span className="vg-bar" style={{ display: 'block', marginTop: 8 }}>
                  <span style={{ width: `${shown[i]}%`, background: st.color }} />
                </span>
                {open && (
                  <span className="vg-log">
                    {a.log
                      .filter(([at]) => shown[i] >= at)
                      .map(([, line], j) => (
                        <span key={line} style={{ animationDelay: `${j * 60}ms` }}>{line}</span>
                      ))}
                  </span>
                )}
              </button>
            );
          })}
          <p className="vg-note">3 jails · 0 lock collisions · your working directory untouched</p>
          <p className="vg-hint">click an agent to tail its log</p>
        </div>
      </WindowFrame>
    </div>
  );
}
