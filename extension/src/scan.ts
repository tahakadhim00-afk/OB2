// Page scan (D-70): the download links in a frame's live page, the same page Chrome's Inspect
// shows. Links, media elements, meta tags, and addresses written anywhere in the HTML, including
// inline scripts (players often keep the file address in JSON, with escaped slashes). Runs in the
// content script, only when the user asks (the popup's "Find downloads") or when the video button
// has nothing else to offer. Nothing is sent anywhere until the user picks a link.

import {
  AUDIO_EXTENSIONS, STREAM_MANIFEST_EXTENSIONS, VIDEO_EXTENSIONS, safeDecode, urlExtension, type MediaCandidate,
} from './media';
import { DEFAULT_RULES } from './rules';

/**
 * The content script puts scanPage under this name in its isolated world (the page can't see it),
 * and the popup calls it in every frame through chrome.scripting.
 */
export const SCAN_GLOBAL = '__ob2ScanPage';

export type FoundKind ='video' | 'audio' | 'file' | 'stream';

export interface FoundLink {
  url: string;
  /** For the list: the `download` attribute's name, else the last part of the address. */
  name: string;
  kind: FoundKind;
  /** The page's own name for the file (`<a download="…">`), sent to OB2 as the file name. */
  downloadName?: string;
}

export interface PageScan {
  pageUrl: string;
  title: string;
  links: FoundLink[];
}

const MAX_LINKS = 300;
/** Larger pages are scanned up to here; the elements above are always read in full. */
const MAX_HTML_CHARS = 5_000_000;

const FILE_EXTENSIONS = new Set(DEFAULT_RULES.extensions);
const ALL_EXTENSIONS = [...new Set([...VIDEO_EXTENSIONS, ...AUDIO_EXTENSIONS, ...STREAM_MANIFEST_EXTENSIONS, ...FILE_EXTENSIONS])]
  .sort((a, b) => b.length - a.length); // longest first, so "docx" wins over "doc"

// An absolute or protocol-relative address whose path ends in a known extension, optionally
// followed by a query. A host name (or IP) must follow the "//", and the path is bounded, so the
// "//" inside base64 data fails within a few characters instead of scanning on (measured in jsdom
// on a 4.8 MB page full of such noise: 1.5 s without the host rule, 64 ms with it).
const STOP = String.raw`\s"'<>()\\\`{}|^`;
const HOST = String.raw`(?:[a-z0-9-]{1,63}\.)+[a-z0-9-]{1,63}(?::\d{1,5})?`;
const URL_IN_TEXT = new RegExp(
  String.raw`(?:https?:)?//${HOST}/[^${STOP}]{0,2000}?\.(?:${ALL_EXTENSIONS.join('|')})(?:[?#][^${STOP}]{0,2000})?(?=[${STOP},;]|$)`,
  'gi',
);

const MEDIA_META = ['og:video', 'og:video:url', 'og:video:secure_url', 'og:audio', 'og:audio:url', 'og:audio:secure_url', 'twitter:player:stream'];

export function kindOfExtension(ext: string | undefined): FoundKind | undefined {
  if (!ext) return undefined;
  if (STREAM_MANIFEST_EXTENSIONS.has(ext)) return 'stream';
  if (VIDEO_EXTENSIONS.has(ext)) return 'video';
  if (AUDIO_EXTENSIONS.has(ext)) return 'audio';
  return FILE_EXTENSIONS.has(ext) ? 'file' : undefined;
}

/**
 * @param loaded media files the background saw this frame load (they have no address in the
 * page when a player feeds the element a blob: URL).
 */
export function scanPage(doc: Document, loaded: readonly MediaCandidate[] = []): PageScan {
  const found = new Map<string, FoundLink>();

  /** `kind` applies when the address has no known extension (a <video> src, a download link). */
  const add = (raw: string | null | undefined, kind?: FoundKind, downloadName?: string) => {
    if (!raw || found.size >= MAX_LINKS) return;
    let url: URL;
    try {
      url = new URL(raw.trim(), doc.baseURI);
    } catch {
      return;
    }
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return; // blob:, data:, javascript:
    url.hash = '';
    const resolved = kindOfExtension(urlExtension(url)) ?? kind;
    if (!resolved || found.has(url.href)) return;
    const name = downloadName?.trim() || safeDecode(url.pathname.split('/').pop() ?? '') || url.hostname;
    const link: FoundLink = { url: url.href, name, kind: resolved };
    if (downloadName?.trim()) link.downloadName = downloadName.trim();
    found.set(url.href, link);
  };

  for (const media of loaded) {
    add(media.url, media.mime?.startsWith('audio/') ? 'audio' : 'video');
  }

  for (const media of Array.from(doc.querySelectorAll<HTMLMediaElement>('video, audio'))) {
    const kind = media.localName === 'audio' ? 'audio' : 'video';
    add(media.currentSrc, kind);
    add(media.getAttribute('src'), kind);
    for (const source of Array.from(media.querySelectorAll('source'))) add(source.getAttribute('src'), kind);
  }
  for (const el of Array.from(doc.querySelectorAll('embed[src], object[data]'))) {
    add(el.getAttribute('src') ?? el.getAttribute('data'));
  }

  for (const meta of Array.from(doc.querySelectorAll<HTMLMetaElement>('meta[property], meta[name]'))) {
    const key = (meta.getAttribute('property') ?? meta.getAttribute('name') ?? '').toLowerCase();
    if (MEDIA_META.includes(key)) add(meta.content, key.includes('audio') ? 'audio' : 'video');
  }

  for (const a of Array.from(doc.querySelectorAll<HTMLAnchorElement | HTMLAreaElement>('a[href], area[href]'))) {
    // A link marked `download` is a file even when its address has no extension (download?id=…).
    const name = a.getAttribute('download');
    add(a.getAttribute('href'), name !== null ? 'file' : undefined, name ?? undefined);
  }

  // Everything else: addresses written in attributes and inline scripts.
  const html = unescapeForScan(doc.documentElement?.outerHTML.slice(0, MAX_HTML_CHARS) ?? '');
  for (const match of html.matchAll(URL_IN_TEXT)) {
    if (found.size >= MAX_LINKS) break;
    add(match[0]);
  }

  return { pageUrl: doc.URL, title: doc.title, links: [...found.values()] };
}

/** JSON and HTML spellings of "/" and "&" in addresses: `https:\/\/cdn/a.mp4?x=1&amp;y=2`. */
function unescapeForScan(text: string): string {
  return text
    .replace(/\\u002f/gi, '/')
    .replace(/\\\//g, '/')
    .replace(/\\u0026/gi, '&')
    .replace(/&amp;/g, '&');
}

export function isPageScan(value: unknown): value is PageScan {
  const s = value as PageScan | undefined;
  return !!s && typeof s.pageUrl === 'string' && typeof s.title === 'string' && Array.isArray(s.links) && s.links.every(isFoundLink);
}

export function isFoundLink(value: unknown): value is FoundLink {
  const l = value as FoundLink | undefined;
  return (
    !!l &&
    typeof l.url === 'string' &&
    /^https?:\/\//i.test(l.url) &&
    typeof l.name === 'string' &&
    ['video', 'audio', 'file', 'stream'].includes(l.kind) &&
    (l.downloadName === undefined || typeof l.downloadName === 'string')
  );
}
