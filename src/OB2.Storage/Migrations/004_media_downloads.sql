-- v1.0: video pages downloaded through yt-dlp (decision D-71). media_format is the yt-dlp format
-- selector the user picked (for example 137+140); NULL for an ordinary file. media_container is
-- the file type the parts are merged into (mp4, webm, mkv) or the single format's extension.

ALTER TABLE downloads ADD COLUMN media_format TEXT;
ALTER TABLE downloads ADD COLUMN media_container TEXT;
