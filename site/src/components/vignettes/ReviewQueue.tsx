import { useState } from 'react';
import { WindowFrame } from './WindowFrame';

interface QueueItem {
  branch: string;
  delta: string;
  agent: string;
  risk: 'low' | 'medium' | 'high';
}

const ITEMS: QueueItem[] = [
  { branch: 'fix/flaky-tests', delta: '+64 −12', agent: 'claude-code', risk: 'low' },
  { branch: 'feat/import-wizard', delta: '+412 −38', agent: 'agy', risk: 'medium' },
  { branch: 'refactor/db-layer', delta: '+840 −790', agent: 'opencode', risk: 'high' },
];

const RISK_COLOR = { low: 'var(--success)', medium: 'var(--warning)', high: 'var(--danger)' } as const;

/** Risk-ranked review queue — click a verified diff to approve it. */
export function ReviewQueueVignette() {
  const [approved, setApproved] = useState<Set<string>>(() => new Set());

  const toggle = (branch: string) =>
    setApproved((prev) => {
      const next = new Set(prev);
      if (next.has(branch)) next.delete(branch);
      else next.add(branch);
      return next;
    });

  return (
    <WindowFrame title="mainguard pro · review queue">
      <div className="vg-grid">
        {ITEMS.map((it) => {
          const ok = approved.has(it.branch);
          return (
            <button
              key={it.branch}
              type="button"
              className={`vg-row ${ok ? 'vg-selected' : ''}`}
              aria-pressed={ok}
              aria-label={`${ok ? 'Undo approval of' : 'Approve'} ${it.branch}`}
              onClick={() => toggle(it.branch)}
              style={{ display: 'block' }}
            >
              <span style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                <span className="mono" style={{ fontSize: 12 }}>{it.branch}</span>
                <span className="mono" style={{ fontSize: 11, color: ok ? 'var(--success)' : 'var(--text-muted)' }}>
                  {ok ? 'approved ✓' : 'verified · awaiting you'}
                </span>
              </span>
              <span style={{ display: 'flex', gap: 8, marginTop: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>{it.delta}</span>
                <span className="mono" style={{ fontSize: 10, color: 'var(--text-muted)', border: '1px solid var(--border-hairline)', borderRadius: 'var(--radius-pill)', padding: '1px 7px' }}>
                  by {it.agent}
                </span>
                <span className="mono" style={{ fontSize: 10, color: RISK_COLOR[it.risk], border: `1px solid ${RISK_COLOR[it.risk]}`, borderRadius: 'var(--radius-pill)', padding: '1px 7px' }}>
                  risk: {it.risk}
                </span>
              </span>
            </button>
          );
        })}
        <p className="vg-note" aria-live="polite">
          {approved.size} of {ITEMS.length} approved · every hunk carries provenance: who wrote it, which prompt, which run
        </p>
        <p className="vg-hint">click a diff to approve it, click again to change your mind</p>
      </div>
    </WindowFrame>
  );
}
