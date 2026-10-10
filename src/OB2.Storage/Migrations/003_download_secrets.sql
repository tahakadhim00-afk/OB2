-- v1.0: "Remember auth" (§11.1, decision D-67). Cookies of unfinished downloads, encrypted with
-- Windows DPAPI for the current user. Rows exist only while the option is on and the download is
-- unfinished; deleting the download deletes its row.

CREATE TABLE download_secrets (
  download_id  INTEGER PRIMARY KEY REFERENCES downloads(id) ON DELETE CASCADE,
  cookies      BLOB NOT NULL,
  updated_at   TEXT NOT NULL
);
