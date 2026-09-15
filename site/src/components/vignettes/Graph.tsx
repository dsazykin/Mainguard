import { useRef, useState } from 'react';
import { WindowFrame } from './WindowFrame';
import { useInView } from '../../lib/hooks';

interface Commit {
  id: string;
  x: number;
  y: number;
  lane: string;
  msg: string;
  hash: string;
  delta: string;
  strong?: boolean;
}

const COMMITS: Commit[] = [
  { id: 'c0', x: 30, y: 20, lane: 'var(--lane-1)', msg: 'refactor commit router', hash: 'a3f9c21', delta: '+64 −12' },
  { id: 'c1', x: 30, y: 60, lane: 'var(--lane-1)', msg: 'stage hunks by selection', hash: 'e8d0b47', delta: '+128 −9' },
  { id: 'c2', x: 30, y: 100, lane: 'var(--lane-1)', msg: 'merge agent/parser · verified ✓', hash: '52c11fe', delta: '+412 −38', strong: true },
  { id: 'c3', x: 70, y: 95, lane: 'var(--lane-2)', msg: 'agent: parser edge cases', hash: '9b30a6d', delta: '+96 −40' },
  { id: 'c4', x: 110, y: 90, lane: 'var(--lane-3)', msg: 'agent: tokenizer tests', hash: '7f4e2ba', delta: '+230 −0' },
  { id: 'c5', x: 30, y: 140, lane: 'var(--lane-1)', msg: 'initial worktree layout', hash: '1d92f03', delta: '+58 −3' },
  { id: 'c6', x: 30, y: 170, lane: 'var(--lane-1)', msg: 'init: scaffold solution', hash: 'f00d5e1', delta: '+840 −0' },
];

/** Commit graph — hover a commit, click to inspect it. Lanes draw in on view. */
export function GraphVignette() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref);
  const [selected, setSelected] = useState<Commit>(COMMITS[2]);

  return (
    <div ref={ref}>
      <WindowFrame title="mainguard · commit graph">
        <svg
          viewBox="0 0 420 190"
          width="100%"
          role="img"
          aria-label="Interactive commit graph with three branch lanes merging"
          className={`vg-draw ${inView ? 'vg-drawn' : ''}`}
          style={{ '--vg-lane-len': 480 } as React.CSSProperties}
        >
          <g fill="none" strokeWidth="2">
            <path className="vg-lane" d="M30 20v150" stroke="var(--lane-1)" />
            <path className="vg-lane" d="M30 45c0 25 40 15 40 40v45c0 25-40 15-40 40" stroke="var(--lane-2)" style={{ animationDelay: '180ms' }} />
            <path className="vg-lane" d="M30 25c0 30 80 20 80 50v30c0 30-80 20-80 50" stroke="var(--lane-3)" style={{ animationDelay: '360ms' }} />
          </g>
          {COMMITS.map((c) => (
            <g
              key={c.id}
              className={`vg-commit ${selected.id === c.id ? 'vg-selected' : ''}`}
              role="button"
              tabIndex={0}
              aria-label={`Commit ${c.hash}: ${c.msg}`}
              onClick={() => setSelected(c)}
              onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && setSelected(c)}
            >
              <circle cx={c.x} cy={c.y} r="9" fill="transparent" stroke="none" />
              <circle
                cx={c.x}
                cy={c.y}
                r={selected.id === c.id ? 6 : 5}
                fill={selected.id === c.id ? c.lane : 'var(--surface-panel)'}
                stroke={c.lane}
                strokeWidth="2"
              />
            </g>
          ))}
          <g fontFamily="var(--font-mono)" fontSize="10.5">
            <rect x="130" y="82" rx="8" width="88" height="17" fill="var(--accent-selection)" stroke="var(--accent)" strokeWidth="1" />
            <text x="141" y="94" fill="var(--accent)">agent/parser</text>
            <rect x="46" y="12" rx="8" width="44" height="17" fill="var(--accent-selection)" stroke="var(--accent)" strokeWidth="1" />
            <text x="57" y="24" fill="var(--accent)">main</text>
          </g>
          <g fontFamily="var(--font-sans)" fontSize="11">
            {[COMMITS[0], COMMITS[1], COMMITS[2], COMMITS[5]].map((c, i) => (
              <text
                key={c.id}
                className="vg-commit-msg"
                x="240"
                y={[24, 64, 95, 144][i]}
                fill={selected.id === c.id ? 'var(--text-primary)' : c.strong ? 'var(--text-primary)' : 'var(--text-muted)'}
                role="button"
                tabIndex={0}
                aria-label={`Select commit: ${c.msg}`}
                onClick={() => setSelected(c)}
                onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && setSelected(c)}
              >
                {c.msg}
              </text>
            ))}
          </g>
        </svg>
        <div className="vg-detail" aria-live="polite">
          <strong>{selected.hash}</strong>
          <span>{selected.msg}</span>
          <span>{selected.delta}</span>
        </div>
        <p className="vg-hint">click any commit. this is real chrome, not a screenshot</p>
      </WindowFrame>
    </div>
  );
}
