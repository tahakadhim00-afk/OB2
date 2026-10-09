-- Phase 4: queue ordering (FR-15), default categories (FR-18), and the default queue (FR-13).

ALTER TABLE downloads ADD COLUMN queue_position INTEGER NOT NULL DEFAULT 0;

CREATE INDEX idx_downloads_queue ON downloads(queue_id, queue_position);

INSERT INTO categories (name, save_folder, extensions) VALUES
  ('Documents',  NULL, 'pdf,doc,docx,xls,xlsx,ppt,pptx,txt,rtf,odt,ods,odp,csv,epub,md'),
  ('Music',      NULL, 'mp3,flac,wav,m4a,aac,ogg,oga,wma,opus,mid'),
  ('Video',      NULL, 'mp4,mkv,avi,mov,wmv,webm,flv,m4v,mpg,mpeg,ts,3gp'),
  ('Programs',   NULL, 'exe,msi,msix,msixbundle,appx,apk,bat,cmd,ps1,dmg,deb,rpm,appimage'),
  ('Compressed', NULL, 'zip,rar,7z,tar,gz,tgz,bz2,xz,zst,iso,img,cab');

INSERT INTO queues (name, concurrency, schedule_json, sort_order) VALUES ('Main', 3, NULL, 0);
