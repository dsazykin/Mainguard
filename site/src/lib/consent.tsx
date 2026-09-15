import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  CONSENT_STORAGE_KEY,
  CONSENT_VERSION,
  NON_ESSENTIAL,
  type ConsentCategory,
} from './consentCategories';

/**
 * Cookie/storage consent.
 *
 * The site currently stores nothing that needs consent, so this is built and
 * wired but dormant: NON_ESSENTIAL is empty, `needsDecision` is always false,
 * and the banner never renders. See consentCategories.ts for the switch.
 *
 * Gate every future non-essential script on `hasConsent(category)` — it
 * returns false until the visitor has actively opted in, so a script written
 * against this API is compliant from its first line.
 */

type Decisions = Partial<Record<ConsentCategory, boolean>>;

interface StoredConsent {
  version: number;
  decidedAt: string;
  decisions: Decisions;
}

interface ConsentContextValue {
  /** The visitor's stored decision, or null if they have not made one. */
  stored: StoredConsent | null;
  /** True when a banner is owed: something non-essential is in use and undecided. */
  needsDecision: boolean;
  /** True when the preferences dialog is open. */
  preferencesOpen: boolean;
  hasConsent: (category: ConsentCategory) => boolean;
  acceptAll: () => void;
  rejectAll: () => void;
  save: (decisions: Decisions) => void;
  openPreferences: () => void;
  closePreferences: () => void;
  /** Whether to show any consent affordance at all (footer control included). */
  consentApplies: boolean;
  /** The browser is sending Global Privacy Control; analytics is off regardless. */
  privacyControlActive: boolean;
}

const ConsentContext = createContext<ConsentContextValue | null>(null);

/**
 * Global Privacy Control: a browser-level "do not sell or share my data"
 * signal the visitor has deliberately switched on. Treated as a standing
 * refusal — no banner is shown, nothing is sent, and the visitor is not
 * nagged to override a preference they already expressed.
 *
 * Legally binding in some jurisdictions and, for a product that sells not
 * doing the creepy thing, the only defensible reading of the signal.
 *
 * Deliberately NOT honouring the older DNT header: browsers have dropped it,
 * some sent it without the user ever choosing, and an ambiguous signal is a
 * bad basis for silently discarding consent either way. GPC is explicit.
 */
function privacyControlEnabled(): boolean {
  try {
    return (navigator as Navigator & { globalPrivacyControl?: boolean }).globalPrivacyControl === true;
  } catch {
    return false;
  }
}

function read(): StoredConsent | null {
  try {
    const raw = localStorage.getItem(CONSENT_STORAGE_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (
      typeof parsed === 'object' &&
      parsed !== null &&
      (parsed as StoredConsent).version === CONSENT_VERSION &&
      typeof (parsed as StoredConsent).decisions === 'object'
    ) {
      return parsed as StoredConsent;
    }
  } catch {
    // unreadable or unavailable storage — treat as no decision
  }
  return null;
}

function write(decisions: Decisions): StoredConsent {
  const record: StoredConsent = {
    version: CONSENT_VERSION,
    decidedAt: new Date().toISOString(),
    decisions,
  };
  try {
    localStorage.setItem(CONSENT_STORAGE_KEY, JSON.stringify(record));
  } catch {
    // best-effort only; the decision still holds for this page view
  }
  return record;
}

export function ConsentProvider({ children }: { children: ReactNode }) {
  const [stored, setStored] = useState<StoredConsent | null>(read);
  const [preferencesOpen, setPreferencesOpen] = useState(false);
  // Read once: the signal does not change mid-visit, and re-reading it on every
  // render would invalidate the memo below for no reason.
  const [gpc] = useState(privacyControlEnabled);

  // The feature still exists under GPC — the footer control stays, so someone
  // can open it and see WHY analytics is off. What GPC suppresses is the
  // banner: nagging someone to override a preference they set is the whole
  // behaviour the signal exists to stop.
  const consentApplies = NON_ESSENTIAL.length > 0;
  const needsDecision = consentApplies && !gpc && stored === null;

  const hasConsent = useCallback(
    (category: ConsentCategory) => {
      if (category === 'necessary') return true;
      // A standing refusal outranks anything stored, including an older opt-in.
      if (gpc) return false;
      return stored?.decisions[category] === true;
    },
    [stored, gpc],
  );

  const save = useCallback((decisions: Decisions) => {
    setStored(write(decisions));
    setPreferencesOpen(false);
  }, []);

  const acceptAll = useCallback(() => {
    save(Object.fromEntries(NON_ESSENTIAL.map((c) => [c, true])));
  }, [save]);

  const rejectAll = useCallback(() => {
    save(Object.fromEntries(NON_ESSENTIAL.map((c) => [c, false])));
  }, [save]);

  // Close the dialog on Escape, matching the nav sheet's behaviour.
  useEffect(() => {
    if (!preferencesOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setPreferencesOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [preferencesOpen]);

  const value = useMemo<ConsentContextValue>(
    () => ({
      stored,
      needsDecision,
      preferencesOpen,
      hasConsent,
      acceptAll,
      rejectAll,
      save,
      openPreferences: () => setPreferencesOpen(true),
      closePreferences: () => setPreferencesOpen(false),
      consentApplies,
      privacyControlActive: gpc,
    }),
    [
      stored,
      needsDecision,
      preferencesOpen,
      hasConsent,
      acceptAll,
      rejectAll,
      save,
      consentApplies,
      gpc,
    ],
  );

  return <ConsentContext.Provider value={value}>{children}</ConsentContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useConsent(): ConsentContextValue {
  const ctx = useContext(ConsentContext);
  if (!ctx) throw new Error('useConsent must be used inside a ConsentProvider');
  return ctx;
}
