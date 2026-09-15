import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView, useReducedMotion, useTicker } from '../../lib/hooks';

const ENTRIES = [
  { n: '#4818', what: 'agent claude-code started · fix/flaky-tests', hash: '9f2a…c418' },
  { n: '#4819', what: 'verification run · 214/214 passed', hash: 'b7d1…03ae' },
  { n: '#4820', what: 'human approved · daniel', hash: '44c8…9b12' },
  { n: '#4821', what: 'merge fix/flaky-tests → main', hash: 'e05f…7d61' },
];

const CYCLE = ENTRIES.length * 5 + 18;

/** Tamper-evident audit log — every event hash-chained to the one before it. */
export function AuditVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const reduced = useReducedMotion();
  const [tick, setTick] = useState(0);

  useTicker(() => setTick((t) => (t + 1) % CYCLE), 260, inView && !reduced);

  const t = reduced ? CYCLE - 1 : tick;
  const shown = Math.min(Math.floor(t / 5) + 1, ENTRIES.length);
  const verified = t >= ENTRIES.length * 5 + 3;

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard pro · audit log">
        <div className="ledger" style={{ padding: '14px 16px', fontSize: '0.78rem', lineHeight: 1.9 }}>
          {ENTRIES.slice(0, shown).map((e, i) => (
            <div key={e.n} className={reduced ? undefined : 'vg-audit-line'} style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              <span className="dim">{e.n} </span>
              {e.what}
              <span className="dim"> · sha256:{e.hash}</span>
              {i > 0 && <span className="dim"> ⇠ chained</span>}
            </div>
          ))}
          {verified && (
            <div className={reduced ? undefined : 'vg-audit-line'}>
              <span className="ok">$ mainguard audit verify · chain intact ✓</span>
            </div>
          )}
        </div>
        <p className="vg-note">who did what, when, and on whose approval. provable after the fact, with one command</p>
      </WindowFrame>
    </div>
  );
}
