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
      'Would measure which pages get read and where people give up. Nothing of the kind is in use today.',
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
 * Categories actually in use that require an opt-in.
 *
 * EMPTY ON PURPOSE. The site stores nothing that needs consent: the theme
 * preference is set by the visitor, and Turnstile is strictly necessary to
 * keep the two forms usable. Showing a consent banner when there is nothing
 * to consent to is both bad manners and misleading.
 *
 * Adding a category here is the single switch that turns the whole consent UI
 * on — banner, preferences dialog and the footer control all follow from it.
 */
export const NON_ESSENTIAL: ConsentCategory[] = [];

export const CONSENT_STORAGE_KEY = 'mainguard-consent';
/** Bump when the categories change — an old decision no longer covers the new set. */
export const CONSENT_VERSION = 1;
