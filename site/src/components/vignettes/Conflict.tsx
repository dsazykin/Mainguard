import { useState } from 'react';
import { WindowFrame } from './WindowFrame';

/** Conflict resolution — click OURS / THEIRS to compose the result. Reversible. */
export function ConflictVignette() {
  const [ours, setOurs] = useState(true);
  const [theirs, setTheirs] = useState(false);

  const result =
    ours && theirs
      ? 'take ours, then theirs'
      : ours
        ? 'take ours'
        : theirs
          ? 'take theirs'
          : 'pick a side, nothing is lost either way';

  return (
    <WindowFrame title="mainguard · resolve · Router.cs">
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
        <button
          type="button"
          className="vg-pane"
          aria-pressed={ours}
          onClick={() => setOurs((v) => !v)}
          style={{ background: 'var(--diff-added-bg)' }}
        >
          <span className="mono" style={{ fontSize: 11, color: 'var(--success)' }}>
            OURS · main {ours && '✓'}
          </span>
          <span style={{ display: 'block', height: 6, width: '85%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 8 }} />
          <span style={{ display: 'block', height: 6, width: '60%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 6 }} />
        </button>
        <button
          type="button"
          className="vg-pane"
          aria-pressed={theirs}
          onClick={() => setTheirs((v) => !v)}
          style={{ background: 'var(--diff-removed-bg)' }}
        >
          <span className="mono" style={{ fontSize: 11, color: 'var(--danger)' }}>
            THEIRS · feature {theirs && '✓'}
          </span>
          <span style={{ display: 'block', height: 6, width: '70%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 8 }} />
          <span style={{ display: 'block', height: 6, width: '90%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 6 }} />
        </button>
      </div>
      <div
        aria-live="polite"
        style={{
          border: `1px solid ${ours || theirs ? 'var(--accent)' : 'var(--border-hairline)'}`,
          background: ours || theirs ? 'var(--accent-selection)' : 'var(--surface-card)',
          borderRadius: 'var(--radius-sm)',
          padding: '10px 12px',
          marginTop: 10,
          transition: 'border-color 200ms var(--ease-out), background-color 200ms var(--ease-out)',
        }}
      >
        <span className="mono" style={{ fontSize: 11, color: ours || theirs ? 'var(--accent)' : 'var(--text-muted)' }}>
          RESULT · {result}
        </span>
      </div>
      <p className="vg-hint">every choice stays reversible until you commit. click the panes</p>
    </WindowFrame>
  );
}
