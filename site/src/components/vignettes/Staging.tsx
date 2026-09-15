import { useState } from 'react';
import { WindowFrame } from './WindowFrame';

interface DiffLine {
  kind: 'add' | 'del' | 'ctx';
  w: number;
}

const LINES: DiffLine[] = [
  { kind: 'ctx', w: 62 },
  { kind: 'del', w: 48 },
  { kind: 'add', w: 55 },
  { kind: 'add', w: 34 },
  { kind: 'ctx', w: 70 },
  { kind: 'add', w: 42 },
  { kind: 'ctx', w: 28 },
];

const CHANGE_COUNT = LINES.filter((l) => l.kind !== 'ctx').length;

/** Partial staging — click changed lines to stage them, then commit. */
export function StagingVignette() {
  const [staged, setStaged] = useState<Set<number>>(() => new Set([2]));
  const [committed, setCommitted] = useState(false);

  const toggle = (i: number) => {
    if (committed) return;
    setStaged((prev) => {
      const next = new Set(prev);
      if (next.has(i)) next.delete(i);
      else next.add(i);
      return next;
    });
  };

  const commit = () => {
    if (staged.size === 0 || committed) return;
    setCommitted(true);
    window.setTimeout(() => {
      setCommitted(false);
      setStaged(new Set());
    }, 1800);
  };

  return (
    <WindowFrame title="mainguard · staging · Parser.cs">
      <div style={{ display: 'grid', gap: 6 }}>
        {LINES.map((l, i) =>
          l.kind === 'ctx' ? (
            <div key={i} className="vg-diffline" style={{ cursor: 'default' }} aria-hidden>
              <span className="vg-gutter" style={{ color: 'var(--text-muted)' }} />
              <span className="vg-linebar" style={{ width: `${l.w}%`, background: 'var(--surface-hover)', opacity: 1 }} />
            </div>
          ) : (
            <button
              key={i}
              type="button"
              className={`vg-diffline ${l.kind}`}
              aria-pressed={staged.has(i)}
              aria-label={`${staged.has(i) ? 'Unstage' : 'Stage'} ${l.kind === 'add' ? 'added' : 'removed'} line`}
              onClick={() => toggle(i)}
            >
              <span className="vg-gutter" style={{ color: l.kind === 'add' ? 'var(--success)' : 'var(--danger)' }}>
                {staged.has(i) ? '✓' : l.kind === 'add' ? '+' : '−'}
              </span>
              <span className="vg-linebar" style={{ width: `${l.w}%` }} />
            </button>
          ),
        )}
        <div style={{ display: 'flex', gap: 8, marginTop: 6, alignItems: 'center', flexWrap: 'wrap' }}>
          <button
            type="button"
            className="pill pill-accent"
            style={{ cursor: 'pointer' }}
            onClick={() => setStaged(new Set(LINES.flatMap((l, i) => (l.kind === 'ctx' ? [] : [i]))))}
          >
            Stage all
          </button>
          <button
            type="button"
            className="pill"
            style={{
              cursor: staged.size > 0 ? 'pointer' : 'not-allowed',
              borderColor: staged.size > 0 ? 'var(--success)' : undefined,
              color: staged.size > 0 ? 'var(--success)' : undefined,
              opacity: staged.size > 0 ? 1 : 0.6,
            }}
            disabled={staged.size === 0}
            onClick={commit}
          >
            Commit {staged.size > 0 ? `${staged.size} line${staged.size > 1 ? 's' : ''}` : '…'}
          </button>
        </div>
        <p className="vg-note" aria-live="polite">
          {committed
            ? '✓ committed b7e21a4 · exactly the lines you picked'
            : `${staged.size} of ${CHANGE_COUNT} changed lines staged · click lines to choose`}
        </p>
      </div>
    </WindowFrame>
  );
}
