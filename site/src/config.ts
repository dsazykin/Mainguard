/**
 * Deployment-specific constants for the Mainguard marketing site.
 *
 * The repo rename has landed (the repo is dsazykin/Mainguard); the archived
 * plan is docs/archive/Mainguard_Rebrand_Plan.md. The old mainguard-site-api
 * worker stays deployed until traffic drains, then gets deleted.
 */
export const API_BASE = 'https://mainguard-site-api.daniel-sazykin.workers.dev';
export const TURNSTILE_SITEKEY = '0x4AAAAAADw_X6swd7JbWQRB';
export const GITHUB_URL = 'https://github.com/dsazykin/Mainguard';
