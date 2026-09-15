import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView, useReducedMotion, useTicker } from '../../lib/hooks';

const PROVIDERS = [
  { name: 'anthropic · your key', base: 38, jitter: 9, color: 'var(--lane-1)', max: 60 },
  { name: 'openai · your key', base: 12, jitter: 5, color: 'var(--lane-5)', max: 60 },
  { name: 'local · ollama', base: 26, jitter: 12, color: 'var(--lane-3)', max: 60 },
];

/** BYOK gateway — request rates tick live while visible. */
export function GatewayVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const reduced = useReducedMotion();
  const [rates, setRates] = useState(() => PROVIDERS.map((p) => p.base));

  useTicker(
    () =>
      setRates((prev) =>
        prev.map((r, i) => {
          const p = PROVIDERS[i];
          const next = r + (Math.random() - 0.5) * p.jitter;
          return Math.max(2, Math.min(p.max - 4, next));
        }),
      ),
    600,
    inView && !reduced,
  );

  const shown = reduced ? PROVIDERS.map((p) => p.base) : rates;

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard pro · gateway">
        <div className="vg-grid">
          {PROVIDERS.map((p, i) => (
            <div key={p.name} className="vg-row" style={{ display: 'block' }}>
              <span style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <span className="mono" style={{ fontSize: 12, display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                  <span style={{ width: 8, height: 8, borderRadius: '50%', background: p.color, flexShrink: 0 }} />
                  {p.name}
                </span>
                <span className="mono" style={{ fontSize: 11, color: 'var(--text-muted)', fontVariantNumeric: 'tabular-nums' }}>
                  {i === 2 ? 'unmetered' : `${Math.round(shown[i])} rpm`}
                </span>
              </span>
              <span className="vg-bar" style={{ display: 'block', marginTop: 8 }}>
                <span style={{ width: `${(shown[i] / p.max) * 100}%`, background: p.color }} />
              </span>
            </div>
          ))}
          <p className="vg-note">burst from one agent absorbed · 0 agents starved · spend per merged change tracked</p>
        </div>
      </WindowFrame>
    </div>
  );
}
