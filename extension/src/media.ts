// Media detection (FR-24, decision D-69): the video and audio files a page loads, offered by the
// button that floats over videos and by the video/audio context menu. OB2 downloads single files,
// so streaming manifests and their segments (HLS, DASH) are left out, as are URLs that return
// only one byte range of a file.

import { parseHttpUrl } from './rules';

export interface MediaCandidate {
  url: string;
  mime?: string;
  /** Size of the whole file in bytes, when the response revealed it. */
  size?: number;
  /**
   * D-71: `url` is a video page, not a file. OB2 asks yt-dlp what it offers and lets the user
   * pick quality and audio (YouTube, and players that stream in pieces).
   */
  page?: boolean;
}

const YOUTUBE_HOSTS = new Set(['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtube-nocookie.com', 'www.youtube-nocookie.com']);

/**
 * The address of one YouTube video (watch, Shorts, live, embed, youtu.be) as
 * `https://www.youtube.com/watch?v=ID`; mirrors VideoSites.YouTubeVideo in the app.
 */
export function youTubeVideoPage(value: string | undefined): string | undefined {
  const url = parseHttpUrl(value);
  if (!url) return undefined;
  const segments = url.pathname.replace(/^\/+|\/+$/g, '').split('/');
  let id: string | null | undefined;
  if (url.hostname === 'youtu.be') {
    id = segments[0];
  } else if (YOUTUBE_HOSTS.has(url.hostname)) {
    if (url.pathname === '/watch') id = url.searchParams.get('v');
    else if (segments.length >= 2 && ['shorts', 'live', 'embed', 'v'].includes(segments[0])) id = segments[1];
  }
  if (!id || !/^[A-Za-z0-9_-]{11}$/.test(id)) return undefined;
  const host = url.hostname === 'music.youtube.com' ? 'music.youtube.com' : 'www.youtube.com';
  return `https://${host}/watch?v=${id}`;
}

/** YouTube pages that are not one video (home, search, channels): no page entry there. */
export function isYouTubeHost(value: string | undefined): boolean {
  const url = parseHttpUrl(value);
  return !!url && (YOUTUBE_HOSTS.has(url.hostname) || url.hostname === 'youtu.be');
}

/** Smaller responses are previews, thumbnails or segments, not something worth a download button. */
export const MEDIA_MIN_BYTES = 1024 * 1024;

export const VIDEO_EXTENSIONS = new Set(['3gp', 'avi', 'flv', 'm4v', 'mkv', 'mov', 'mp4', 'ogv', 'webm', 'wmv']);
export const AUDIO_EXTENSIONS = new Set(['aac', 'flac', 'm4a', 'mp3', 'oga', 'ogg', 'opus', 'wav', 'wma']);
const MEDIA_EXTENSIONS = new Set([...VIDEO_EXTENSIONS, ...AUDIO_EXTENSIONS]);
/** Playlists a page may name; worth listing (so the user sees why they can't be downloaded). */
export const STREAM_MANIFEST_EXTENSIONS = new Set(['f4m', 'm3u8', 'mpd']);
const STREAM_EXTENSIONS = new Set([...STREAM_MANIFEST_EXTENSIONS, 'ism', 'm4s', 'ts']);
const STREAM_MIMES = new Set([
  'application/dash+xml', 'application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl',
  'audio/x-mpegurl', 'audio/iso.segment', 'video/iso.segment', 'video/mp2t', 'video/vnd.mpeg.dash.mpd',
]);
const GENERIC_MIMES = new Set(['', 'application/octet-stream', 'binary/octet-stream', 'application/binary']);
/** Query parameters that make a URL return one chunk of a file (YouTube `range`, Facebook `bytestart`). */
const CHUNK_PARAMS = ['range', 'bytestart', 'byteend'];

const MIME_EXTENSIONS: Record<string, string> = {
  'video/mp4': 'mp4',
  'video/webm': 'webm',
  'video/x-matroska': 'mkv',
  'video/quicktime': 'mov',
  'video/ogg': 'ogv',
  'video/x-flv': 'flv',
  'video/3gpp': '3gp',
  'audio/mpeg': 'mp3',
  'audio/mp4': 'm4a',
  'audio/aac': 'aac',
  'audio/flac': 'flac',
  'audio/wav': 'wav',
  'audio/x-wav': 'wav',
  'audio/ogg': 'ogg',
  'audio/webm': 'webm',
};

/** The parts of chrome.webRequest's response details the detection uses. */
export interface ResponseFacts {
  url: string;
  statusCode: number;
  responseHeaders?: Array<{ name: string; value?: string }>;
}

export function mediaFromResponse(details: ResponseFacts): MediaCandidate | undefined {
  if (details.statusCode !== 200 && details.statusCode !== 206) return undefined;
  const url = parseHttpUrl(details.url);
  if (!url || CHUNK_PARAMS.some((p) => url.searchParams.has(p))) return undefined;

  const header = (name: string) => details.responseHeaders?.find((h) => h.name.toLowerCase() === name)?.value;
  const mime = (header('content-type') ?? '').toLowerCase().split(';')[0].trim();
  const ext = urlExtension(url);
  if (STREAM_MIMES.has(mime) || (ext && STREAM_EXTENSIONS.has(ext))) return undefined;
  const isMedia = mime.startsWith('video/') || mime.startsWith('audio/') || (GENERIC_MIMES.has(mime) && !!ext && MEDIA_EXTENSIONS.has(ext));
  if (!isMedia) return undefined;

  // A 206 reply's Content-Length is the chunk; the whole size is after the slash in Content-Range.
  const size = details.statusCode === 206 ? rangeTotal(header('content-range')) : positive(header('content-length'));
  if (size !== undefined && size < MEDIA_MIN_BYTES) return undefined;

  const candidate: MediaCandidate = { url: url.href };
  if (mime && !GENERIC_MIMES.has(mime)) candidate.mime = mime;
  if (size !== undefined) candidate.size = size;
  return candidate;
}

/** A source address OB2 can download as one file: http(s), not a streaming manifest or segment. */
export function isDownloadableMediaUrl(value: string | undefined): boolean {
  const url = parseHttpUrl(value);
  if (!url || CHUNK_PARAMS.some((p) => url.searchParams.has(p))) return false;
  const ext = urlExtension(url);
  return !ext || !STREAM_EXTENSIONS.has(ext);
}

/** Messages from other contexts are checked before use. */
export function isMediaCandidate(value: unknown): value is MediaCandidate {
  const m = value as MediaCandidate | undefined;
  return (
    !!m &&
    typeof m.url === 'string' &&
    isDownloadableMediaUrl(m.url) &&
    (m.mime === undefined || typeof m.mime === 'string') &&
    (m.size === undefined || (typeof m.size === 'number' && m.size > 0)) &&
    (m.page === undefined || typeof m.page === 'boolean')
  );
}

/**
 * A file name from the page title when the URL has none worth keeping (`videoplayback?id=…`),
 * so the saved file is recognizable. Undefined when the URL already names a media file or the type is unknown.
 */
export function mediaFileName(media: MediaCandidate, pageTitle: string | undefined): string | undefined {
  const url = parseHttpUrl(media.url);
  const urlExt = url ? urlExtension(url) : undefined;
  if (urlExt && MEDIA_EXTENSIONS.has(urlExt)) return undefined;
  const ext = MIME_EXTENSIONS[media.mime ?? ''];
  // Slashes would read as folders; the app sanitizes the other characters Windows rejects.
  const title = (pageTitle ?? '').replace(/[\\/]+/g, '-').replace(/\s+/g, ' ').trim().slice(0, 120).trim();
  return ext && title ? `${title}.${ext}` : undefined;
}

/** "clip.mp4 · 24 MB" for the button's menu; the host name stands in for a missing file name. */
export function mediaLabel(media: MediaCandidate, formatBytes: (bytes: number) => string): string {
  if (media.page) return 'Choose quality… (whole video)';
  const url = parseHttpUrl(media.url);
  const name = (url ? safeDecode(url.pathname.split('/').pop() ?? '') : '') || url?.hostname || media.url;
  const kind = media.mime ? media.mime.split('/')[0] : undefined;
  const parts = [name.length > 48 ? `${name.slice(0, 45)}…` : name];
  if (media.size) parts.push(formatBytes(media.size));
  else if (kind === 'video' || kind === 'audio') parts.push(kind);
  return parts.join(' · ');
}

export function urlExtension(url: URL): string | undefined {
  const base = safeDecode(url.pathname).split('/').pop() ?? '';
  const dot = base.lastIndexOf('.');
  return dot > 0 && dot < base.length - 1 ? base.slice(dot + 1).toLowerCase() : undefined;
}

function rangeTotal(value: string | undefined): number | undefined {
  const match = /\/\s*(\d+)\s*$/.exec(value ?? '');
  return match ? positive(match[1]) : undefined;
}

function positive(value: string | undefined): number | undefined {
  const n = Number(value);
  return value && Number.isFinite(n) && n > 0 ? n : undefined;
}

export function safeDecode(path: string): string {
  try {
    return decodeURIComponent(path);
  } catch {
    return path;
  }
}
