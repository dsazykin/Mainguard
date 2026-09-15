import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView, useReducedMotion, useTicker } from '../../lib/hooks';

const GATES = ['build', 'tests 214/214', 'lint', 'review'];
// Each gate: ~5 ticks running, then pass. Tick = 220ms.
const TICKS_PER_GATE = 5;
const DONE_AT = GATES.length * TICKS_PER_GATE;
const RESET_AT = DONE_AT + 12;

/** The merge gate — gates run in sequence, and red never lands. Click to replay. */
export function PipelineVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const reduced = useReducedMotion();
  const [tick, setTick] = useState(0);

  useTicker(() => setTick((t) => (t >= RESET_AT ? 0 : t + 1)), 220, inView && !reduced);

  const t = reduced ? DONE_AT : tick;
  const gateState = (i: number): 'pending' | 'running' | 'pass' => {
    if (t >= (i + 1) * TICKS_PER_GATE) return 'pass';
    if (t >= i * TICKS_PER_GATE) return 'running';
    return 'pending';
  };
  const merged = t >= DONE_AT;

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard pro · verify & merge">
        <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 8 }}>
          {GATES.map((label, i) => {
            const st = gateState(i);
            return (
              <span key={label} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <span className={`vg-gate ${st === 'pending' ? '' : st}`}>
                  {st === 'pass' ? '✓ ' : st === 'running' ? '● ' : '○ '}
                  {label}
                </span>
                {i < GATES.length - 1 && <span style={{ color: 'var(--text-muted)' }}>→</span>}
              </span>
            );
          })}
        </div>
        <p className="vg-note" aria-live="polite">
          {merged
            ? '✓ all gates green · fix/flaky-tests merged to main'
            : 'merge blocked until every gate passes. nothing lands unverified'}
        </p>
        {!reduced && (
          <button type="button" className="vg-replay" style={{ marginTop: 10 }} onClick={() => setTick(0)}>
            ↻ replay run
          </button>
        )}
      </WindowFrame>
    </div>
  );
}
