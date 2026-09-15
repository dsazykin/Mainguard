import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { Link } from 'react-router';
import { useConsent } from '../lib/consent';
import { CATEGORIES, NON_ESSENTIAL, type ConsentCategory } from '../lib/consentCategories';

/**
 * The consent banner and preferences dialog.
 *
 * Both are dormant while NON_ESSENTIAL is empty — see lib/consent.tsx. The
 * banner renders only when a decision is actually owed, and the dialog only
 * when the visitor asks for it, so today this component returns null.
 */
export function CookieConsent() {
  const { needsDecision, preferencesOpen, closePreferences, acceptAll, rejectAll, save, stored } =
    useConsent();

  if (!needsDecision && !preferencesOpen) return null;

  return (
    <>
      {needsDecision && !preferencesOpen && (
        <ConsentBanner onAcceptAll={acceptAll} onRejectAll={rejectAll} />
      )}
      {preferencesOpen &&
        createPortal(
          <ConsentDialog
            initial={stored?.decisions ?? {}}
            onClose={closePreferences}
            onSave={save}
            onAcceptAll={acceptAll}
            onRejectAll={rejectAll}
          />,
          document.body,
        )}
    </>
  );
}

function ConsentBanner({
  onAcceptAll,
  onRejectAll,
}: {
  onAcceptAll: () => void;
  onRejectAll: () => void;
}) {
  const { openPreferences } = useConsent();

  return createPortal(
    <div className="consent-bar" role="dialog" aria-modal="false" aria-label="Cookie choices">
      <div className="consent-bar-inner">
        <p className="consent-copy">
          Mainguard uses storage that is strictly necessary to run the site. Anything beyond that
          is off until you allow it. Read the{' '}
          <Link to="/privacy">privacy policy</Link> for what that covers.
        </p>
        <div className="consent-actions">
          {/* Reject is given equal weight to accept, deliberately: a
              reject-by-dark-pattern banner is not consent. */}
          <button type="button" className="btn btn-quiet" onClick={onRejectAll}>
            Reject non-essential
          </button>
          <button type="button" className="btn btn-quiet" onClick={openPreferences}>
            Choose
          </button>
          <button type="button" className="btn btn-accent" onClick={onAcceptAll}>
            Allow all
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

function ConsentDialog({
  initial,
  onClose,
  onSave,
  onAcceptAll,
  onRejectAll,
}: {
  initial: Partial<Record<ConsentCategory, boolean>>;
  onClose: () => void;
  onSave: (d: Partial<Record<ConsentCategory, boolean>>) => void;
  onAcceptAll: () => void;
  onRejectAll: () => void;
}) {
  const [draft, setDraft] = useState<Partial<Record<ConsentCategory, boolean>>>(initial);
  const { privacyControlActive } = useConsent();

  useEffect(() => {
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = '';
    };
  }, []);

  return (
    <div className="consent-scrim" onClick={onClose}>
      <div
        className="consent-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="consent-title"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="consent-title" className="consent-title">
          Cookie settings
        </h2>
        <p className="consent-copy">
          Mainguard sets nothing that tracks you. These switches exist so that stays true if that
          ever changes.
        </p>

        {privacyControlActive && (
          <div className="legal-callout" style={{ margin: 'var(--space-4) 0 0' }}>
            <p style={{ margin: 0, fontSize: 'var(--text-sm)', lineHeight: 1.7 }}>
              Your browser is sending <strong>Global Privacy Control</strong>, so analytics is
              already off and nothing is being sent. There is nothing here you need to change.
            </p>
          </div>
        )}

        <div className="consent-list">
          {CATEGORIES.map((c) => {
            const inUse = c.required || NON_ESSENTIAL.includes(c.id);
            // Under GPC the effective answer is no, whatever is stored from
            // before. The switch has to show the truth, or it is telling the
            // visitor they are being counted when they are not.
            const on = c.required || (!privacyControlActive && draft[c.id] === true);
            return (
              <div key={c.id} className="consent-item">
                <div className="consent-item-head">
                  <span className="consent-item-label">{c.label}</span>
                  {c.required ? (
                    <span className="pill">Always on</span>
                  ) : inUse ? (
                    <button
                      type="button"
                      role="switch"
                      aria-checked={on}
                      disabled={privacyControlActive}
                      aria-label={`${c.label} — ${
                        privacyControlActive
                          ? 'blocked by Global Privacy Control'
                          : on
                            ? 'allowed'
                            : 'blocked'
                      }`}
                      className={`consent-switch ${on ? 'is-on' : ''}`}
                      onClick={() => setDraft((d) => ({ ...d, [c.id]: !on }))}
                    >
                      <span className="consent-knob" aria-hidden />
                    </button>
                  ) : (
                    <span className="pill">Not in use</span>
                  )}
                </div>
                <p className="consent-item-desc">{c.description}</p>
              </div>
            );
          })}
        </div>

        {/* Offering "Allow all" under GPC would be offering a button that does
            nothing, since the signal overrides whatever gets stored. */}
        <div className="consent-actions consent-actions-end">
          {privacyControlActive ? (
            <button type="button" className="btn btn-accent" onClick={onClose}>
              Close
            </button>
          ) : (
            <>
              <button type="button" className="btn btn-quiet" onClick={onRejectAll}>
                Reject non-essential
              </button>
              <button type="button" className="btn btn-quiet" onClick={onAcceptAll}>
                Allow all
              </button>
              <button type="button" className="btn btn-accent" onClick={() => onSave(draft)}>
                Save choices
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
