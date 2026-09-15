import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView, useReducedMotion, useTicker } from '../../lib/hooks';

const PROMPTS = [
  'A booking page for my pottery studio, with calendar, deposits and email reminders.',
  'An inventory tracker for the shop, with barcode scans and low-stock alerts.',
  'A newsletter site with paid subscriptions and a tip jar.',
];

const TYPE_TICK = 45; // ms per character
const BUILD_TICKS = 46; // ~2s of building after typing completes
const DONE_TICKS = 60; // hold the finished state, then next prompt

/** Cloud — a prompt is typed, agents build, verified product comes out. Loops. */
export function CloudVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const reduced = useReducedMotion();
  const [promptIdx, setPromptIdx] = useState(0);
  const [tick, setTick] = useState(0);

  const prompt = PROMPTS[promptIdx];
  const typeDone = prompt.length;
  const buildDone = typeDone + BUILD_TICKS;
  const allDone = buildDone + DONE_TICKS;

  useTicker(
    () =>
      setTick((t) => {
        if (t + 1 >= allDone) {
          setPromptIdx((i) => (i + 1) % PROMPTS.length);
          return 0;
        }
        return t + 1;
      }),
    TYPE_TICK,
    inView && !reduced,
  );

  const t = reduced ? buildDone : tick;
  const typed = prompt.slice(0, Math.min(t, typeDone));
  const building = t >= typeDone && t < buildDone;
  const done = t >= buildDone;

  const replay = () => {
    setPromptIdx((i) => (i + 1) % PROMPTS.length);
    setTick(0);
  };

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard cloud · new request">
        <div
          style={{
            border: '1px solid var(--border-hairline)',
            borderRadius: 'var(--radius-md)',
            padding: '12px 14px',
            background: 'var(--surface-card)',
            fontSize: 14,
            color: 'var(--text-primary)',
            minHeight: '4.4em',
          }}
          aria-label={`Prompt: ${prompt}`}
        >
          “{typed}
          {!reduced && t < typeDone && <span className="vg-caret" aria-hidden />}
          {t >= typeDone && '”'}
        </div>
        <svg
          viewBox="0 0 420 70"
          width="100%"
          aria-hidden
          className={`vg-draw ${t >= typeDone || reduced ? 'vg-drawn' : ''}`}
          style={{ display: 'block', margin: '6px 0', '--vg-lane-len': 420 } as React.CSSProperties}
        >
          <g fill="none" strokeWidth="1.8">
            <path className="vg-lane" d="M60 6 C 130 6, 150 35, 210 35 S 300 64, 360 64" stroke="var(--lane-1)" />
            <path className="vg-lane" d="M210 6 C 250 6, 260 35, 210 35 S 160 64, 210 64" stroke="var(--lane-2)" style={{ animationDelay: '160ms' }} />
            <path className="vg-lane" d="M360 6 C 290 6, 270 35, 210 35 S 120 64, 60 64" stroke="var(--lane-3)" style={{ animationDelay: '320ms' }} />
          </g>
        </svg>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', minHeight: '2.1em' }} aria-live="polite">
          {building && (
            <span className="pill" style={{ borderColor: 'var(--accent)', color: 'var(--accent)' }}>
              ● 3 agents building…
            </span>
          )}
          {done && (
            <>
              <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
                ✓ 3 agents finished
              </span>
              <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
                ✓ cleared the gate
              </span>
              <span className="pill pill-accent">Preview your site →</span>
            </>
          )}
        </div>
        {!reduced && (
          <button type="button" className="vg-replay" style={{ marginTop: 10 }} onClick={replay}>
            ↻ ask for another
          </button>
        )}
      </WindowFrame>
    </div>
  );
}
