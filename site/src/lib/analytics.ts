import { API_BASE } from '../config';

/**
 * First-party analytics.
 *
 * Deliberately thin, and the constraints are the point:
 *
 * - **Nothing is stored on your device.** No analytics cookie, no localStorage
 *   id. Unique visitors are counted server-side with a hash that includes the
 *   calendar date, so it cannot follow anyone from one day to the next.
 * - **No third party.** Events go to Mainguard's own Cloudflare Worker. No
 *   Google, no Meta pixel, no script from anyone else's domain.
 * - **Opt-in only.** Every call here is gated on the visitor having allowed
 *   analytics; with no consent, `send` returns before doing anything.
 * - **It can never break the page.** Failures are swallowed, the request is
 *   fire-and-forget, and the worker always answers 204.
 *
 * What the site learns: which pages are read, where readers came from, which
 * country and device class, which theme, and which calls to action get
 * clicked. What it cannot learn: who you are, or what you did yesterday.
 */

type EventType = 'pageview' | 'cta';

interface EventPayload {
  type: EventType;
  path: string;
  referrer?: string;
  campaign?: string;
  theme?: string;
  label?: string;
}

/**
 * Set by the consent layer. Analytics is off until something explicitly turns
 * it on, so a bug that fails to wire consent fails closed, not open.
 */
let allowed = false;

export function setAnalyticsAllowed(next: boolean): void {
  allowed = next;
}

/** The campaign tag from ?utm_campaign= or ?ref=, if the link carried one. */
function campaignFromUrl(): string | undefined {
  try {
    const p = new URLSearchParams(window.location.search);
    return p.get('utm_campaign') ?? p.get('ref') ?? undefined;
  } catch {
    return undefined;
  }
}

function currentTheme(): string | undefined {
  return document.documentElement.dataset.theme ?? undefined;
}

function send(payload: EventPayload): void {
  if (!allowed) return;
  try {
    const body = JSON.stringify(payload);
    const url = `${API_BASE}/api/event`;
    // sendBeacon survives the page being closed, which a fetch on unload does
    // not. It is also fire-and-forget, so it cannot delay a navigation.
    if (navigator.sendBeacon?.(url, new Blob([body], { type: 'application/json' }))) return;
    void fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      keepalive: true,
    }).catch(() => {
      // analytics must never surface an error to the visitor
    });
  } catch {
    // ditto — a thrown beacon is not worth a broken page
  }
}

export function trackPageview(path: string): void {
  send({
    type: 'pageview',
    path,
    // document.referrer is the previous page; the worker keeps only its host.
    referrer: document.referrer || undefined,
    campaign: campaignFromUrl(),
    theme: currentTheme(),
  });
}

export function trackCta(label: string, path: string): void {
  send({ type: 'cta', path, label, theme: currentTheme() });
}
