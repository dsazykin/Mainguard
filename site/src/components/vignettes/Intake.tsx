import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView, useReducedMotion, useTicker } from '../../lib/hooks';

const PRS = [
  { id: 'codex · PR #412', title: 'feat: bulk export', appearAt: 0 },
  { id: 'jules · PR #87', title: 'fix: date parsing', appearAt: 6 },
  { id: 'copilot · PR #93', title: 'chore: dep bumps', appearAt: 12 },
];

const STATES = ['queued', 'verifying…', 'verified ✓'] as const;
const STATE_COLOR = ['var(--text-muted)', 'var(--warning)', 'var(--success)'];
const CYCLE = 44;

/** External agent PRs flowing into the same verify → review → merge pipeline. */
export function IntakeVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const reduced = useReducedMotion();
  const [tick, setTick] = useState(0);

  useTicker(() => setTick((t) => (t + 1) % CYCLE), 260, inView && !reduced);

  const t = reduced ? CYCLE - 1 : tick;

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard pro · external intake">
        <div className="vg-grid">
          {PRS.map((pr) => {
            if (t < pr.appearAt) return null;
            const age = t - pr.appearAt;
            const stage = Math.min(Math.floor(age / 7), 2);
            return (
              <div key={pr.id} className="vg-row" style={{ animation: reduced ? undefined : 'vg-log-in 300ms var(--ease-out) backwards' }}>
                <span className="mono" style={{ fontSize: 12, display: 'inline-flex', gap: 10, alignItems: 'baseline', minWidth: 0 }}>
                  <span style={{ color: 'var(--text-primary)', whiteSpace: 'nowrap' }}>{pr.id}</span>
                  <span style={{ color: 'var(--text-muted)', fontSize: 11, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{pr.title}</span>
                </span>
                <span className="mono" style={{ fontSize: 11, color: STATE_COLOR[stage], flexShrink: 0, transition: 'color 300ms var(--ease-out)' }}>
                  {STATES[stage]}
                </span>
              </div>
            );
          })}
          <p className="vg-note">
            any agent's PRs, one pipeline: same gates, same review queue, same audit trail
          </p>
        </div>
      </WindowFrame>
    </div>
  );
}
