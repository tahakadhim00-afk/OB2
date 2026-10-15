-- v1.0: a fingerprint of the first bytes of the entity, used to tell whether a file changed
-- between a pause and a resume when the server offers no validator. ETag and Last-Modified are
-- preferred; this only decides the case where the server sends neither and the length happens to
-- match, which would otherwise resume onto different bytes and produce a corrupt file.
-- head_sha256 is the hex SHA-256 of the first head_bytes bytes; both NULL when not yet known.

ALTER TABLE downloads ADD COLUMN head_sha256 TEXT;
ALTER TABLE downloads ADD COLUMN head_bytes INTEGER;
