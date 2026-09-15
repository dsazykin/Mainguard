/**
 * Deployment-specific constants for the Mainguard marketing site.
 *
 * The GitHub rename has landed: dsazykin/GitLoom now redirects to
 * dsazykin/Mainguard, so the canonical URL is used directly here.
 */
export const API_BASE = 'https://mainguard-site-api.daniel-sazykin.workers.dev';
export const TURNSTILE_SITEKEY = '0x4AAAAAADw_X6swd7JbWQRB';
export const GITHUB_URL = 'https://github.com/dsazykin/Mainguard';
export const GITHUB_REPO_LABEL = 'dsazykin/Mainguard';

/**
 * Legal identity shown on the privacy policy and terms.
 *
 * CONFIRM BEFORE RELYING ON THESE. They are deliberately conservative: a
 * natural person operating under a product name, which is true today. If a
 * company is ever incorporated, or if a registered address, KvK number or VAT
 * number becomes legally required for this site, they belong here — the two
 * legal pages read every one of these from this file, so one edit updates both.
 *
 * Dutch consumer-facing sites generally must publish the trader's identity and
 * a registered address; a contact form alone may not be sufficient. That is a
 * question for a lawyer, not for this comment.
 */
/**
 * Full legal name, deliberately spelled Daniil. The anglicised "Daniel" shows
 * up in the git identity, the notification address and the workers.dev
 * subdomain; none of those is the legal name, and this field has to be.
 */
export const LEGAL_ENTITY = 'Daniil Sazykin, trading as Mainguard';
/**
 * Published contact address for privacy requests and legal notices.
 *
 * Both the GDPR (controller contact details) and the Dutch implementation of
 * the E-Commerce Directive want an address that reaches a human quickly; a
 * contact form on its own probably does not satisfy the latter. A personal
 * address is used deliberately until an @mainguard.dev mailbox exists — a
 * published address that bounces is worse than a personal one that works.
 * Swap this one constant when the domain mailbox is ready.
 */
export const LEGAL_CONTACT_EMAIL = 'daniel.sazykin@gmail.com';
export const LEGAL_JURISDICTION = 'the Netherlands';
/** Governing law for the terms of service. */
export const LEGAL_GOVERNING_LAW = 'Dutch law';
/** Courts with jurisdiction over disputes under the terms. */
export const LEGAL_VENUE = 'the competent court in the Netherlands';
/** Bump whenever either legal page changes in substance. */
export const LEGAL_LAST_UPDATED = '15 September 2026';
