-- GitLoom site submissions. Applied with:
--   npx wrangler d1 execute gitloom-site --remote --file=schema.sql
CREATE TABLE IF NOT EXISTS submissions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  kind TEXT NOT NULL CHECK (kind IN ('waitlist', 'contact')),
  email TEXT NOT NULL,
  name TEXT,
  topic TEXT,
  message TEXT,
  interests TEXT,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  ip_hash TEXT,
  user_agent TEXT
);

-- One waitlist row per email; repeat signups update interests instead.
CREATE UNIQUE INDEX IF NOT EXISTS idx_waitlist_email
  ON submissions (email) WHERE kind = 'waitlist';

-- Rate-limit lookups.
CREATE INDEX IF NOT EXISTS idx_ip_time ON submissions (ip_hash, created_at);

-- ————— Analytics —————
--
-- Aggregate-only, and deliberately thin. There is no cookie, no device
-- identifier and no raw IP here: `visitor_day` is a salted hash that includes
-- the calendar date, so it groups a person's hits WITHIN one day and is
-- useless for following them across days. Nothing in this table identifies a
-- person, and nothing joins to `submissions`.
--
-- Only written when the visitor has opted in to analytics.
CREATE TABLE IF NOT EXISTS events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  -- Only these two. Scroll-depth tracking was considered and dropped: the
  -- privacy policy promises no scroll tracking, and that promise is worth more
  -- than knowing how far down the Pro page people get.
  type TEXT NOT NULL CHECK (type IN ('pageview', 'cta')),
  path TEXT NOT NULL,
  -- Referrer HOST only ('news.ycombinator.com'), never the full URL, which can
  -- carry the search terms or a private page title.
  referrer_host TEXT,
  campaign TEXT,
  country TEXT,
  device TEXT,
  theme TEXT,
  -- 'waitlist-hero', 'waitlist-pro', … for type='cta'. Null for a pageview.
  label TEXT,
  visitor_day TEXT,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_events_type_time ON events (type, created_at);
CREATE INDEX IF NOT EXISTS idx_events_path_time ON events (path, created_at);
CREATE INDEX IF NOT EXISTS idx_events_visitor_day ON events (visitor_day, created_at);
