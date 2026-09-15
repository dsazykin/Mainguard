/**
 * Mainguard site API — waitlist + contact form backend.
 * (Deployed worker name and URL still say gitloom until redeployed — see the rebrand plan.)
 *
 * Endpoints:
 *   POST /api/waitlist            { email, interests?, turnstileToken, website? }
 *   POST /api/contact             { name, email, topic?, message, turnstileToken, website? }
 *   POST /api/event               { type, path, referrer?, campaign?, theme?, label? }
 *   GET  /api/admin/submissions   Authorization: Bearer <ADMIN_TOKEN>; ?kind=waitlist|contact&limit=N
 *   GET  /api/admin/stats         Authorization: Bearer <ADMIN_TOKEN>; ?days=N
 *
 * `website` is a honeypot: real users never fill it; bots that do get a fake success.
 * Every real submission is Turnstile-verified, rate-limited per IP, and stored in D1.
 * If RESEND_API_KEY is configured, a notification email is sent to NOTIFY_EMAIL.
 */

export interface Env {
  DB: D1Database;
  TURNSTILE_SECRET: string;
  ADMIN_TOKEN: string;
  NOTIFY_EMAIL: string;
  RESEND_API_KEY?: string;
}

const ALLOWED_ORIGINS = new Set([
  'https://mainguard.dev',
  'https://www.mainguard.dev',
  'https://dsazykin.github.io', // legacy Pages origin — drop after the domain cutover settles
  'http://localhost:5173',
  'http://localhost:4173',
]);

const RATE_LIMIT_PER_HOUR = 10;
// `weave` is Mainguard Cloud's original wire id; `cloud` is accepted so the
// site can migrate to it once this worker version is deployed.
const VALID_INTERESTS = new Set(['client', 'pro', 'weave', 'cloud']);
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

function corsHeaders(origin: string | null): Record<string, string> {
  const allowed = origin && ALLOWED_ORIGINS.has(origin) ? origin : 'https://mainguard.dev';
  return {
    'Access-Control-Allow-Origin': allowed,
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type, Authorization',
    'Access-Control-Max-Age': '86400',
    Vary: 'Origin',
  };
}

function json(body: unknown, status: number, origin: string | null): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) },
  });
}

async function sha256(input: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(input));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

async function verifyTurnstile(env: Env, token: string, ip: string): Promise<boolean> {
  if (!token) return false;
  const res = await fetch('https://challenges.cloudflare.com/turnstile/v0/siteverify', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ secret: env.TURNSTILE_SECRET, response: token, remoteip: ip }),
  });
  if (!res.ok) return false;
  const data = (await res.json()) as { success: boolean };
  return data.success === true;
}

async function isRateLimited(env: Env, ipHash: string): Promise<boolean> {
  const row = await env.DB.prepare(
    "SELECT COUNT(*) AS n FROM submissions WHERE ip_hash = ?1 AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '-1 hour')",
  )
    .bind(ipHash)
    .first<{ n: number }>();
  return (row?.n ?? 0) >= RATE_LIMIT_PER_HOUR;
}

async function notify(env: Env, subject: string, text: string): Promise<void> {
  if (!env.RESEND_API_KEY) return;
  try {
    await fetch('https://api.resend.com/emails', {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${env.RESEND_API_KEY}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        from: 'Mainguard Site <onboarding@resend.dev>',
        to: [env.NOTIFY_EMAIL],
        subject,
        text,
      }),
    });
  } catch {
    // Notification failure must never fail the submission.
  }
}

type SubmissionBody = {
  email?: unknown;
  name?: unknown;
  topic?: unknown;
  message?: unknown;
  interests?: unknown;
  turnstileToken?: unknown;
  website?: unknown; // honeypot
};

function str(v: unknown, max: number): string | null {
  if (typeof v !== 'string') return null;
  const trimmed = v.trim();
  return trimmed.length > 0 && trimmed.length <= max ? trimmed : null;
}

async function guard(
  env: Env,
  request: Request,
  body: SubmissionBody,
  origin: string | null,
): Promise<{ ipHash: string } | Response> {
  // Honeypot: filled means bot — return a fake success and store nothing.
  if (typeof body.website === 'string' && body.website.trim() !== '') {
    return json({ ok: true }, 200, origin);
  }
  const ip = request.headers.get('CF-Connecting-IP') ?? '0.0.0.0';
  const ipHash = await sha256(`gitloom:${ip}`);
  if (await isRateLimited(env, ipHash)) {
    return json({ ok: false, error: 'Too many submissions. Please try again later.' }, 429, origin);
  }
  if (!(await verifyTurnstile(env, str(body.turnstileToken, 4096) ?? '', ip))) {
    return json(
      { ok: false, error: 'Anti-spam verification failed. Please refresh and try again.' },
      403,
      origin,
    );
  }
  return { ipHash };
}

async function handleWaitlist(request: Request, env: Env, origin: string | null): Promise<Response> {
  const body = (await request.json().catch(() => ({}))) as SubmissionBody;
  const email = str(body.email, 254)?.toLowerCase();
  if (!email || !EMAIL_RE.test(email)) {
    return json({ ok: false, error: 'Please enter a valid email address.' }, 400, origin);
  }
  const interests = Array.isArray(body.interests)
    ? body.interests.filter((i): i is string => typeof i === 'string' && VALID_INTERESTS.has(i))
    : [];

  const guarded = await guard(env, request, body, origin);
  if (guarded instanceof Response) return guarded;

  const ua = (request.headers.get('User-Agent') ?? '').slice(0, 256);
  await env.DB.prepare(
    `INSERT INTO submissions (kind, email, interests, ip_hash, user_agent)
     VALUES ('waitlist', ?1, ?2, ?3, ?4)
     ON CONFLICT (email) WHERE kind = 'waitlist'
     DO UPDATE SET interests = ?2`,
  )
    .bind(email, JSON.stringify(interests), guarded.ipHash, ua)
    .run();

  await notify(
    env,
    'Mainguard waitlist signup',
    `${email}\nInterests: ${interests.join(', ') || '(none selected)'}`,
  );
  return json({ ok: true }, 200, origin);
}

async function handleContact(request: Request, env: Env, origin: string | null): Promise<Response> {
  const body = (await request.json().catch(() => ({}))) as SubmissionBody;
  const email = str(body.email, 254)?.toLowerCase();
  const name = str(body.name, 100);
  const topic = str(body.topic, 100);
  const message = str(body.message, 5000);
  if (!email || !EMAIL_RE.test(email)) {
    return json({ ok: false, error: 'Please enter a valid email address.' }, 400, origin);
  }
  if (!name) return json({ ok: false, error: 'Please enter your name.' }, 400, origin);
  if (!message) {
    return json({ ok: false, error: 'Please enter a message (max 5000 characters).' }, 400, origin);
  }

  const guarded = await guard(env, request, body, origin);
  if (guarded instanceof Response) return guarded;

  const ua = (request.headers.get('User-Agent') ?? '').slice(0, 256);
  await env.DB.prepare(
    `INSERT INTO submissions (kind, email, name, topic, message, ip_hash, user_agent)
     VALUES ('contact', ?1, ?2, ?3, ?4, ?5, ?6)`,
  )
    .bind(email, name, topic, message, guarded.ipHash, ua)
    .run();

  await notify(
    env,
    `Mainguard contact: ${topic ?? 'General'}`,
    `From: ${name} <${email}>\nTopic: ${topic ?? '(none)'}\n\n${message}`,
  );
  return json({ ok: true }, 200, origin);
}

const EVENT_TYPES = new Set(['pageview', 'cta', 'depth']);
/** Paths the SPA can legitimately report. Anything else is recorded as 'other'. */
const KNOWN_PATHS = new Set([
  '/',
  '/client',
  '/pro',
  '/cloud',
  '/contact',
  '/waitlist',
  '/privacy',
  '/terms',
]);
const VALID_THEMES = new Set(['midnight', 'daylight', 'graphite', 'atelier']);
/** Per-visitor-day event ceiling, so a loop cannot flood the table. */
const EVENT_LIMIT_PER_DAY = 300;

/**
 * A visitor key that is useless tomorrow.
 *
 * Salting with the calendar date means the same person hashes differently each
 * day: good enough to count unique visitors per day, structurally incapable of
 * following anyone across days. The IP itself is never stored.
 */
async function visitorDayHash(ip: string, ua: string): Promise<string> {
  const day = new Date().toISOString().slice(0, 10);
  return (await sha256(`mainguard:${day}:${ip}:${ua}`)).slice(0, 32);
}

/** Desktop / mobile / tablet, which is all the device detail worth keeping. */
function deviceClass(ua: string): string {
  if (/iPad|Tablet/i.test(ua)) return 'tablet';
  if (/Mobi|Android|iPhone/i.test(ua)) return 'mobile';
  return 'desktop';
}

/** Referrer host only — a full URL can leak search terms or a private title. */
function referrerHost(raw: string | undefined, selfHost: string): string | null {
  if (!raw) return null;
  try {
    const host = new URL(raw).hostname.replace(/^www\./, '');
    return host === selfHost.replace(/^www\./, '') ? null : host.slice(0, 120);
  } catch {
    return null;
  }
}

interface EventBody {
  type?: unknown;
  path?: unknown;
  referrer?: unknown;
  campaign?: unknown;
  theme?: unknown;
  label?: unknown;
}

/**
 * Analytics ingest. Fired only by visitors who opted in — the site does not
 * call this otherwise. Always answers 204 so a blocked or failed beacon never
 * shows the visitor an error; analytics must never be able to break the page.
 */
async function handleEvent(request: Request, env: Env, origin: string | null): Promise<Response> {
  const noContent = () => new Response(null, { status: 204, headers: corsHeaders(origin) });

  let body: EventBody;
  try {
    body = (await request.json()) as EventBody;
  } catch {
    return noContent();
  }

  const type = str(body.type, 16);
  if (!type || !EVENT_TYPES.has(type)) return noContent();

  const rawPath = str(body.path, 256) ?? '/';
  const path = KNOWN_PATHS.has(rawPath) ? rawPath : 'other';

  const ip = request.headers.get('CF-Connecting-IP') ?? '0.0.0.0';
  const ua = request.headers.get('User-Agent') ?? '';
  const visitor = await visitorDayHash(ip, ua);

  const { results } = await env.DB.prepare(
    "SELECT COUNT(*) AS n FROM events WHERE visitor_day = ?1 AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '-1 day')",
  )
    .bind(visitor)
    .all();
  if (Number((results[0] as { n: number } | undefined)?.n ?? 0) >= EVENT_LIMIT_PER_DAY) {
    return noContent();
  }

  const theme = str(body.theme, 16);
  const selfHost = origin ? new URL(origin).hostname : 'mainguard.dev';

  await env.DB.prepare(
    `INSERT INTO events (type, path, referrer_host, campaign, country, device, theme, label, visitor_day)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)`,
  )
    .bind(
      type,
      path,
      referrerHost(str(body.referrer, 2048) ?? undefined, selfHost),
      str(body.campaign, 64),
      // Country comes from Cloudflare's edge, so no IP geolocation is done here.
      (request as Request & { cf?: { country?: string } }).cf?.country ?? null,
      deviceClass(ua),
      theme && VALID_THEMES.has(theme) ? theme : null,
      str(body.label, 64),
      visitor,
    )
    .run();

  return noContent();
}

/** Aggregates only — this endpoint cannot return a single visitor's trail. */
async function handleStats(request: Request, env: Env, origin: string | null): Promise<Response> {
  const auth = request.headers.get('Authorization') ?? '';
  if (auth !== `Bearer ${env.ADMIN_TOKEN}`) {
    return json({ ok: false, error: 'Unauthorized.' }, 401, origin);
  }
  const url = new URL(request.url);
  const days = Math.min(Math.max(Number(url.searchParams.get('days')) || 30, 1), 365);
  const since = `-${days} days`;

  const group = async (sql: string) => {
    const { results } = await env.DB.prepare(sql).bind(since).all();
    return results;
  };

  const [pages, referrers, countries, devices, themes, ctas, daily, totals] = await Promise.all([
    group(
      "SELECT path, COUNT(*) AS views, COUNT(DISTINCT visitor_day) AS visitors FROM events WHERE type='pageview' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY path ORDER BY views DESC",
    ),
    group(
      "SELECT COALESCE(referrer_host,'(direct)') AS source, COUNT(*) AS views FROM events WHERE type='pageview' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY source ORDER BY views DESC LIMIT 25",
    ),
    group(
      "SELECT COALESCE(country,'??') AS country, COUNT(DISTINCT visitor_day) AS visitors FROM events WHERE type='pageview' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY country ORDER BY visitors DESC LIMIT 40",
    ),
    group(
      "SELECT device, COUNT(DISTINCT visitor_day) AS visitors FROM events WHERE type='pageview' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY device ORDER BY visitors DESC",
    ),
    group(
      "SELECT theme, COUNT(DISTINCT visitor_day) AS visitors FROM events WHERE type='pageview' AND theme IS NOT NULL AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY theme ORDER BY visitors DESC",
    ),
    group(
      "SELECT label, COUNT(*) AS clicks FROM events WHERE type='cta' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY label ORDER BY clicks DESC",
    ),
    group(
      "SELECT substr(created_at,1,10) AS day, COUNT(*) AS views, COUNT(DISTINCT visitor_day) AS visitors FROM events WHERE type='pageview' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1) GROUP BY day ORDER BY day DESC",
    ),
    group(
      "SELECT (SELECT COUNT(*) FROM events WHERE type='pageview' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1)) AS pageviews, (SELECT COUNT(*) FROM submissions WHERE kind='waitlist' AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ','now',?1)) AS waitlist_signups",
    ),
  ]);

  return json(
    { ok: true, days, totals: totals[0] ?? {}, pages, referrers, countries, devices, themes, ctas, daily },
    200,
    origin,
  );
}

async function handleAdmin(request: Request, env: Env, origin: string | null): Promise<Response> {
  const auth = request.headers.get('Authorization') ?? '';
  if (auth !== `Bearer ${env.ADMIN_TOKEN}`) {
    return json({ ok: false, error: 'Unauthorized.' }, 401, origin);
  }
  const url = new URL(request.url);
  const kind = url.searchParams.get('kind');
  const limit = Math.min(Number(url.searchParams.get('limit')) || 100, 1000);
  const query =
    kind === 'waitlist' || kind === 'contact'
      ? env.DB.prepare(
          'SELECT id, kind, email, name, topic, message, interests, created_at FROM submissions WHERE kind = ?1 ORDER BY id DESC LIMIT ?2',
        ).bind(kind, limit)
      : env.DB.prepare(
          'SELECT id, kind, email, name, topic, message, interests, created_at FROM submissions ORDER BY id DESC LIMIT ?1',
        ).bind(limit);
  const { results } = await query.all();
  return json({ ok: true, count: results.length, submissions: results }, 200, origin);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const origin = request.headers.get('Origin');
    const { pathname } = new URL(request.url);

    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders(origin) });
    }

    try {
      if (request.method === 'POST' && pathname === '/api/waitlist') {
        return await handleWaitlist(request, env, origin);
      }
      if (request.method === 'POST' && pathname === '/api/contact') {
        return await handleContact(request, env, origin);
      }
      if (request.method === 'POST' && pathname === '/api/event') {
        return await handleEvent(request, env, origin);
      }
      if (request.method === 'GET' && pathname === '/api/admin/submissions') {
        return await handleAdmin(request, env, origin);
      }
      if (request.method === 'GET' && pathname === '/api/admin/stats') {
        return await handleStats(request, env, origin);
      }
      return json({ ok: false, error: 'Not found.' }, 404, origin);
    } catch (err) {
      console.error('Unhandled error', err);
      return json({ ok: false, error: 'Something went wrong. Please try again.' }, 500, origin);
    }
  },
} satisfies ExportedHandler<Env>;
