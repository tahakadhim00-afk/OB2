-- Schema v1 (PRD §10.1).
-- Applied migrations are immutable: change the schema by adding 002_*.sql, never by editing this file.

CREATE TABLE categories (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  name            TEXT NOT NULL UNIQUE,
  save_folder     TEXT,
  extensions      TEXT                 -- comma-separated
);

CREATE TABLE queues (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  name            TEXT NOT NULL UNIQUE,
  concurrency     INTEGER NOT NULL DEFAULT 3,
  schedule_json   TEXT,                -- weekdays, start/stop times
  sort_order      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE downloads (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  url              TEXT NOT NULL,
  final_url        TEXT,
  filename         TEXT NOT NULL,
  dest_path        TEXT NOT NULL,
  category_id      INTEGER REFERENCES categories(id),
  queue_id         INTEGER REFERENCES queues(id),
  status           TEXT NOT NULL,      -- queued|probing|downloading|paused|retrying|completed|failed|cancelled
  total_bytes      INTEGER,            -- NULL if unknown
  downloaded_bytes INTEGER NOT NULL DEFAULT 0,
  connections      INTEGER NOT NULL DEFAULT 1,
  supports_ranges  INTEGER NOT NULL DEFAULT 0,
  etag             TEXT,
  last_modified    TEXT,
  checksum_algo    TEXT,               -- md5|sha256|NULL
  checksum_value   TEXT,
  referrer         TEXT,
  user_agent       TEXT,
  headers_json     TEXT,               -- non-sensitive headers only
  speed_limit_bps  INTEGER,
  error_code       TEXT,
  error_message    TEXT,
  created_at       TEXT NOT NULL,      -- ISO 8601 UTC
  started_at       TEXT,
  completed_at     TEXT,
  source           TEXT NOT NULL       -- manual|chrome|clipboard|cli
);

CREATE TABLE segments (
  download_id     INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
  segment_index   INTEGER NOT NULL,
  range_start     INTEGER NOT NULL,
  range_end       INTEGER NOT NULL,    -- inclusive
  downloaded      INTEGER NOT NULL DEFAULT 0,
  status          TEXT NOT NULL,       -- pending|active|done|failed
  retry_count     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (download_id, segment_index)
);

CREATE TABLE history_events (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  download_id     INTEGER REFERENCES downloads(id) ON DELETE SET NULL,
  event_type      TEXT NOT NULL,
  detail          TEXT,
  occurred_at     TEXT NOT NULL
);

CREATE INDEX idx_downloads_status ON downloads(status);
CREATE INDEX idx_downloads_created ON downloads(created_at);
