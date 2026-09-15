#!/usr/bin/env node
/**
 * Print the site analytics in a form a human can read.
 *
 * The admin endpoint returns JSON, which is right for a machine and useless
 * over morning coffee. This turns it into tables.
 *
 *   ADMIN_TOKEN=… npm run stats          # last 30 days
 *   ADMIN_TOKEN=… npm run stats -- 7     # last 7 days
 *
 * API_BASE overrides the worker URL when testing against a local `wrangler dev`.
 */

const API_BASE = process.env.API_BASE ?? 'https://mainguard-site-api.daniel-sazykin.workers.dev';
const token = process.env.ADMIN_TOKEN;
const days = Number(process.argv[2]) || 30;

if (!token) {
  console.error('Set ADMIN_TOKEN (the same value as `wrangler secret put ADMIN_TOKEN`).');
  process.exit(1);
}

const res = await fetch(`${API_BASE}/api/admin/stats?days=${days}`, {
  headers: { Authorization: `Bearer ${token}` },
});

if (!res.ok) {
  console.error(`${res.status} ${res.statusText} — ${await res.text()}`);
  process.exit(1);
}

const s = await res.json();

const bold = (t) => `[1m${t}[0m`;
const dim = (t) => `[2m${t}[0m`;

/** A left-aligned label column with right-aligned numbers, and a bar. */
function table(title, rows, labelKey, valueKeys) {
  console.log(`\n${bold(title)}`);
  if (!rows?.length) {
    console.log(dim('  (nothing yet)'));
    return;
  }
  const label = (r) => String(r[labelKey] ?? '—');
  const width = Math.min(Math.max(...rows.map((r) => label(r).length)), 38);
  const max = Math.max(...rows.map((r) => Number(r[valueKeys[0]]) || 0), 1);
  for (const r of rows) {
    const name = label(r).slice(0, width).padEnd(width);
    const nums = valueKeys.map((k) => String(r[k] ?? 0).padStart(6)).join('');
    // 24-column bar, scaled to the largest row.
    const bar = '█'.repeat(Math.round(((Number(r[valueKeys[0]]) || 0) / max) * 24));
    console.log(`  ${name}${nums}  ${dim(bar)}`);
  }
}

const t = s.totals ?? {};
const e = s.engagement ?? {};
console.log(bold(`\nMainguard site — last ${s.days} days`));
console.log(`  pageviews          ${t.pageviews ?? 0}`);
console.log(`  visitors           ${t.visitors ?? 0} ${dim('(per-day uniques)')}`);
console.log(`  waitlist signups   ${t.waitlist_signups ?? 0}`);

// The number that actually matters: how many readers turned into signups.
const pv = Number(t.pageviews) || 0;
const su = Number(t.waitlist_signups) || 0;
if (pv > 0) console.log(`  signup rate        ${((su / pv) * 100).toFixed(2)}% of pageviews`);
console.log(`  pages per visitor  ${e.pages_per_visitor ?? 0}`);
console.log(`  single-page visits ${e.single_page_pct ?? 0}%`);

table('Landed on', s.entryPages, 'path', ['visitors']);
table('Pages', s.pages, 'path', ['views', 'visitors']);
table('Referrers', s.referrers, 'source', ['views']);
table('Campaigns', s.campaigns, 'campaign', ['visitors', 'views']);
table('Countries', s.countries, 'country', ['visitors']);
table('Devices', s.devices, 'device', ['visitors']);
table('Themes', s.themes, 'theme', ['visitors']);
table('CTA clicks', s.ctas, 'label', ['clicks']);
table('Contact form — reached', s.formSteps, 'label', ['reached']);
table('Dead URLs hit', s.notFound, 'url', ['hits', 'visitors']);
table('By day', s.daily, 'day', ['views', 'visitors']);

console.log(
  dim(
    '\n  Columns are views then unique visitors, except where only one applies.\n' +
      '  Uniques are per day and cannot be summed across days — the same person\n' +
      '  visiting twice in a week counts twice. Obvious bots are dropped before\n' +
      '  they are recorded, and analytics is opt-in (and off entirely for anyone\n' +
      '  sending Global Privacy Control), so every number here undercounts.\n' +
      '  Read them as trends, not totals.\n',
  ),
);
