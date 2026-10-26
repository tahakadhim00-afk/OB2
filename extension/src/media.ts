// Media detection (FR-24, decision D-69): the video and audio files a page loads, offered by the
// button that floats over videos and by the video/audio context menu. OB2 downloads single files,
// so streaming manifests and their segments (HLS, DASH) are left out, as are URLs that return
// only one byte range of a file. D-79: the manifests are kept apart, for yt-dlp to read when it
// doesn't know the site.

import type { MediaOption, MediaSubtitle } from './protocol';
import { parseHttpUrl } from './rules';

export interface MediaCandidate {
  url: string;
  mime?: string;
  /** Size of the whole file in bytes, when the response revealed it. */
  size?: number;
  /**
   * D-71: `url` is a video page, not a file. OB2 asks yt-dlp what it offers and lets the user
   * pick quality and audio (YouTube, and players that stream in pieces). D-79: or the stream
   * such a player loaded, when yt-dlp read that instead of the page.
   */
  page?: boolean;
  /**
   * D-72: with `page`, one quality picked on the button's menu: yt-dlp's format selector, the
   * finished file's extension, and its name (the video's title). D-74: or a subtitle track.
   */
  format?: string;
  container?: string;
  name?: string;
  /** D-80: with a stream (`page`), the request headers the browser sent for it. */
  headers?: RequestHeaders;
}

/** D-80: request headers by name, as the browser sent them. */
export type RequestHeaders = Record<string, string>;

/** D-80: a stream the frame's player loaded, with the request headers the browser sent for it. */
export interface StreamInfo {
  url: string;
  headers: RequestHeaders;
}

const FORMAT_SPEC = /^[A-Za-z0-9_][A-Za-z0-9_.=@-]{0,79}(?:\+[A-Za-z0-9_][A-Za-z0-9_.=@-]{0,79})?$/;
const STREAM_SPEC = /^stream:(?:v\d{1,3}(?:\+a\d{1,3})?|a\d{1,3})$/;
const CONTAINER = /^[a-z0-9]{1,5}$/;
const SUBTITLE_SPEC = /^(?:auto-)?subs:[A-Za-z0-9][A-Za-z0-9_-]{0,34}$/;
const SUBTITLE_CONTAINER = /^(?:srt|ass|vtt)$/;

/**
 * D-72: a format and container OB2 may have offered; mirrors ProtocolValidator.IsMediaChoice in
 * the app. D-74: subtitle tracks (`subs:en`, `auto-subs:en-orig`) go only with srt, ass or vtt.
 * D-80: tracks of a stream OB2 reads itself are `stream:v1+a0`.
 */
export function isMediaChoice(format: unknown, container: unknown): boolean {
  if (typeof format !== 'string' || typeof container !== 'string') return false;
  return SUBTITLE_SPEC.test(format) ? SUBTITLE_CONTAINER.test(container) : (FORMAT_SPEC.test(format) || STREAM_SPEC.test(format)) && CONTAINER.test(container);
}

/** Mirrors ProtocolValidator.MaxHeaders and its header checks in the app. */
const MAX_HEADERS = 50;
const HEADER_NAME = /^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$/;
/** No line breaks or other control characters but tab: the value goes into a request as it is. */
const HEADER_VALUE = /^[^\x00-\x08\x0a-\x1f\x7f]{0,8192}$/;
/**
 * Headers OB2 sets itself for each request (Range per piece, the connection, compression), and
 * credentials it keeps apart: cookies are read from the browser's store when they are needed,
 * and Authorization is never kept (§11.1).
 */
const UNKEPT_HEADERS = new Set([
  'accept-encoding', 'authorization', 'connection', 'content-length', 'content-type', 'cookie', 'expect', 'host', 'if-modified-since',
  'if-none-match', 'if-range', 'keep-alive', 'proxy-authorization', 'proxy-connection', 'range', 'te', 'transfer-encoding', 'upgrade',
]);

/**
 * D-80: what the browser sent for a stream, as OB2 should send it again. Players' servers check
 * more than the address: movie-box.co's refused anything without the token header its player
 * adds, its Origin and its Referer.
 */
export function requestHeadersOf(list: ReadonlyArray<{ name: string; value?: string }> | undefined): RequestHeaders {
  const headers: RequestHeaders = {};
  let count = 0;
  for (const { name, value } of list ?? []) {
    if (count >= MAX_HEADERS || value === undefined || UNKEPT_HEADERS.has(name.toLowerCase())) continue;
    if (!HEADER_NAME.test(name) || !HEADER_VALUE.test(value)) continue;
    headers[name] = value;
    count++;
  }
  return headers;
}

/** Messages from other contexts are checked before use. */
export function isRequestHeaders(value: unknown): value is RequestHeaders {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const entries = Object.entries(value);
  return entries.length <= MAX_HEADERS && entries.every(([name, v]) => HEADER_NAME.test(name) && typeof v === 'string' && HEADER_VALUE.test(v));
}

export function isStreamInfo(value: unknown): value is StreamInfo {
  const s = value as StreamInfo | undefined;
  return !!s && isStreamAddress(s.url) && isRequestHeaders(s.headers);
}

/** D-80: the Referer and User-Agent travel in fields of their own; the rest as headers. */
export function splitRequestHeaders(headers: RequestHeaders): { referrer?: string; userAgent?: string; headers: RequestHeaders } {
  const result: { referrer?: string; userAgent?: string; headers: RequestHeaders } = { headers: {} };
  for (const [name, value] of Object.entries(headers)) {
    const lower = name.toLowerCase();
    if (lower === 'referer') result.referrer = value;
    else if (lower === 'user-agent') result.userAgent = value;
    else result.headers[name] = value;
  }
  return result;
}

/** D-74: one subtitle track of a `media_formats` reply, with only the containers OB2 may have offered. */
export function mediaSubtitleOf(value: unknown): MediaSubtitle | undefined {
  const s = value as MediaSubtitle | undefined;
  if (!s || typeof s.format !== 'string' || !SUBTITLE_SPEC.test(s.format) || typeof s.language !== 'string' || typeof s.name !== 'string') return undefined;
  if (typeof s.automatic !== 'boolean' || !Array.isArray(s.containers)) return undefined;
  const containers = s.containers.filter((c): c is string => isMediaChoice(s.format, c));
  return containers.length > 0 ? { format: s.format, language: s.language, name: s.name.trim() || s.language, automatic: s.automatic, containers } : undefined;
}

/** "English", or "English (Original) · auto-generated" for captions the site's speech recognition made (D-74). */
export function subtitleLabel(subtitle: MediaSubtitle): string {
  return subtitle.automatic ? `${subtitle.name} · auto-generated` : subtitle.name;
}

/** One entry of a `media_formats` reply, checked before the menu shows it. */
export function isMediaOption(value: unknown): value is MediaOption {
  const o = value as MediaOption | undefined;
  const optional = (v: unknown, type: 'string' | 'number') => v === undefined || v === null || typeof v === type;
  return (
    !!o &&
    isMediaChoice(o.format, o.container) &&
    typeof o.audioOnly === 'boolean' &&
    optional(o.quality, 'string') &&
    optional(o.codec, 'string') &&
    optional(o.track, 'string') &&
    optional(o.size, 'number')
  );
}

/** "1080p60 · MP4 (H.264) · 163 MB", or "Audio only · M4A (AAC) · 129 kbit/s · 3.4 MB" (D-72). */
export function optionLabel(option: MediaOption, formatBytes: (bytes: number) => string): string {
  const type = option.codec ? `${option.container.toUpperCase()} (${option.codec})` : option.container.toUpperCase();
  const parts = option.audioOnly ? [option.track ? `Audio only (${option.track})` : 'Audio only', type, option.quality] : [option.quality || 'Video', type];
  if (option.size && option.size > 0) parts.push(formatBytes(option.size));
  return parts.filter(Boolean).join(' · ');
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
const MANIFEST_MIMES = new Set([
  'application/dash+xml', 'application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl', 'video/vnd.mpeg.dash.mpd',
]);
const GENERIC_MIMES = new Set(['', 'application/octet-stream', 'binary/octet-stream', 'application/binary']);
/** Mirrors ProtocolValidator.MaxUrlLength in the app. */
const MAX_URL_LENGTH = 2048;
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

/**
 * D-79: an HLS or DASH manifest a player loaded (`master.m3u8`, `manifest.mpd`), by its type or
 * by its extension when the server doesn't say. yt-dlp reads one like a page; its segments are
 * no use on their own.
 */
export function streamFromResponse(details: ResponseFacts): string | undefined {
  if (details.statusCode !== 200) return undefined;
  const url = parseHttpUrl(details.url);
  if (!url || url.href.length > MAX_URL_LENGTH) return undefined;
  const type = details.responseHeaders?.find((h) => h.name.toLowerCase() === 'content-type')?.value;
  const mime = (type ?? '').toLowerCase().split(';')[0].trim();
  if (MANIFEST_MIMES.has(mime)) return url.href;
  const ext = urlExtension(url);
  // An error page or a segment under a manifest's name is not one.
  const other = mime === 'text/html' || /^(?:video|audio|image)\//.test(mime);
  return ext && STREAM_MANIFEST_EXTENSIONS.has(ext) && !other ? url.href : undefined;
}

/** D-79: a stream the frame reported, checked before it goes to OB2. */
export function isStreamAddress(value: unknown): value is string {
  return typeof value === 'string' && value.length <= MAX_URL_LENGTH && !!parseHttpUrl(value);
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
    // D-79: a video page may be a stream, which goes to yt-dlp, never to Chrome.
    (m.page === true ? isStreamAddress(m.url) : isDownloadableMediaUrl(m.url)) &&
    (m.mime === undefined || typeof m.mime === 'string') &&
    (m.size === undefined || (typeof m.size === 'number' && m.size > 0)) &&
    (m.page === undefined || typeof m.page === 'boolean') &&
    (m.format === undefined ? m.container === undefined : m.page === true && isMediaChoice(m.format, m.container)) &&
    (m.headers === undefined || (m.page === true && isRequestHeaders(m.headers))) &&
    (m.name === undefined || (typeof m.name === 'string' && m.name.length <= 300))
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
