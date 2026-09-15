/**
 * Consent categories and storage constants.
 *
 * Kept apart from the provider in consent.tsx so that file exports only
 * components and hooks — mixing constants in there breaks fast refresh.
 */

export type ConsentCategory = 'necessary' | 'analytics' | 'marketing';

export interface CategoryInfo {
  id: ConsentCategory;
  label: string;
  description: string;
  /** Necessary storage cannot be declined, so its toggle is locked on. */
  required: boolean;
}

export const CATEGORIES: CategoryInfo[] = [
  {
    id: 'necessary',
    label: 'Strictly necessary',
    description:
      'Remembers the theme you pick, and lets Cloudflare Turnstile tell a person from a bot when you submit a form. Never used to track you, and cannot be switched off without breaking the site.',
    required: true,
  },
  {
    id: 'analytics',
    label: 'Analytics',
    description:
      'Counts which pages are read, which link sent you here, your country and device type, and which buttons get clicked. First-party only: it sets nothing on your device and is never shared. Declining costs you nothing.',
    required: false,
  },
  {
    id: 'marketing',
    label: 'Marketing',
    description:
      'Would attribute a signup to the link that produced it. Nothing of the kind is in use today.',
    required: false,
  },
];

/**
 * Categories actually in use that require an opt-in. This is the single switch
 * that drives the banner, the preferences dialog and the footer control.
 *
 * `analytics` is live: first-party, cookieless, aggregate-only page and CTA
 * counts (see lib/analytics.ts). It sets nothing on the device, so a consent
 * banner is arguably not required for it at all — it is asked for anyway,
 * because "we could have skipped asking" is a poor thing to explain later.
 */
export const NON_ESSENTIAL: ConsentCategory[] = ['analytics'];

export const CONSENT_STORAGE_KEY = 'mainguard-consent';
/** Bump when the categories change — an old decision no longer covers the new set. */
export const CONSENT_VERSION = 1;
