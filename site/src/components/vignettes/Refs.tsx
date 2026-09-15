import { useState } from 'react';
import { WindowFrame } from './WindowFrame';

const REFS = [
  { name: 'main', color: 'var(--lane-1)', note: 'worktree · ~/code/app', branch: true },
  { name: 'feature/import-wizard', color: 'var(--lane-2)', note: 'worktree · ~/code/app-wt1', branch: true },
  { name: 'fix/flaky-tests', color: 'var(--lane-3)', note: '2 ahead', branch: true },
  { name: 'v2.4.0', color: 'var(--lane-4)', note: 'tag', branch: false },
];

/** Refs panel — click a branch to check it out (HEAD moves). */
export function RefsVignette() {
  const [head, setHead] = useState('main');

  return (
    <WindowFrame title="mainguard · refs">
      <div className="vg-grid">
        {REFS.map((r) =>
          r.branch ? (
            <button
              key={r.name}
              type="button"
              className={`vg-row ${head === r.name ? 'vg-selected' : ''}`}
              onClick={() => setHead(r.name)}
              aria-label={`Check out ${r.name}`}
            >
              <span className="mono" style={{ fontSize: 12.5, display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <span style={{ width: 8, height: 8, borderRadius: '50%', background: r.color, flexShrink: 0 }} />
                {r.name}
                {head === r.name && (
                  <span className="mono" style={{ fontSize: 9.5, color: 'var(--accent)', border: '1px solid var(--accent)', borderRadius: 'var(--radius-pill)', padding: '1px 6px' }}>
                    HEAD
                  </span>
                )}
              </span>
              <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>{r.note}</span>
            </button>
          ) : (
            <div key={r.name} className="vg-row">
              <span className="mono" style={{ fontSize: 12.5, display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <span style={{ width: 8, height: 8, borderRadius: '50%', background: r.color, flexShrink: 0 }} />
                {r.name}
              </span>
              <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>{r.note}</span>
            </div>
          ),
        )}
        <p className="vg-hint">click a branch to check it out, without touching your other worktrees</p>
      </div>
    </WindowFrame>
  );
}
