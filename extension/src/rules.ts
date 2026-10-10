// Capture rules (§8.4): minimum size, file types, excluded domains. The app owns them
// (Options > Browser); the extension caches the copy that arrives with each status reply.

import type { CaptureRules } from './protocol';

/** Same defaults as CaptureSettings in the app, used until the app has been reached once. */
export const DEFAULT_RULES: CaptureRules = {
  enabled: true,
  minSizeBytes: 1024 * 1024,
  extensions: [
    '7z', 'apk', 'avi', 'bin', 'bz2', 'deb', 'dmg', 'doc', 'docx', 'exe', 'flac', 'gz', 'img',
    'iso', 'm4a', 'mkv', 'mov', 'mp3', 'mp4', 'msi', 'pdf', 'ppt', 'pptx', 'rar', 'tar', 'wav',
    'webm', 'xls', 'xlsx', 'xz', 'zip',
  ],
  excludedDomains: [],
};

/** Refresh the cached rules when they are older than this. */
export const RULES_MAX_AGE_MS = 10 * 60 * 1000;

export interface StoredSettings {
  /** The popup's on/off switch. Stored locally; no browsing data is stored (§11.2). */
  captureEnabled: boolean;
  rules: CaptureRules;
  rulesFetchedAt: number;
}

export async function loadSettings(): Promise<StoredSettings> {
  const stored = (await chrome.storage.local.get(['captureEnabled', 'rules', 'rulesFetchedAt'])) as Partial<StoredSettings>;
  return {
    captureEnabled: stored.captureEnabled ?? true,
    rules: isRules(stored.rules) ? stored.rules : DEFAULT_RULES,
    rulesFetchedAt: stored.rulesFetchedAt ?? 0,
  };
}

export async function saveRules(rules: CaptureRules, now = Date.now()): Promise<void> {
  if (isRules(rules)) {
    await chrome.storage.local.set({ rules, rulesFetchedAt: now });
  }
}

export async function setCaptureEnabled(enabled: boolean): Promise<void> {
  await chrome.storage.local.set({ captureEnabled: enabled });
}

/** The popup's "Show a download button on videos" switch (FR-24). On unless turned off. */
export const MEDIA_BUTTON_KEY = 'mediaButtonEnabled';

export async function loadMediaButtonEnabled(): Promise<boolean> {
  const stored = (await chrome.storage.local.get([MEDIA_BUTTON_KEY])) as Record<string, unknown>;
  return stored[MEDIA_BUTTON_KEY] !== false;
}

export async function setMediaButtonEnabled(enabled: boolean): Promise<void> {
  await chrome.storage.local.set({ [MEDIA_BUTTON_KEY]: enabled });
}

function isRules(value: unknown): value is CaptureRules {
  const r = value as CaptureRules | undefined;
  return !!r && typeof r.enabled === 'boolean' && typeof r.minSizeBytes === 'number' && Array.isArray(r.extensions) && Array.isArray(r.excludedDomains);
}

/** The parts of chrome.downloads.DownloadItem the decision uses. */
export interface DownloadFacts {
  url: string;
  finalUrl?: string;
  referrer?: string;
  filename?: string;
  mime?: string;
  totalBytes?: number;
  fileSize?: number;
  state?: string;
  incognito?: boolean;
  byExtensionId?: string;
}

export type SkipReason =
  | 'switched-off'
  | 'disabled-in-app'
  | 'started-by-extension'
  | 'incognito'
  | 'not-in-progress'
  | 'not-http'
  | 'shift-click'
  | 'excluded-domain'
  | 'too-small'
  | 'file-type';

export type Decision = { capture: true } | { capture: false; reason: SkipReason };

export function decide(item: DownloadFacts, settings: StoredSettings, bypassed: boolean): Decision {
  const skip = (reason: SkipReason): Decision => ({ capture: false, reason });
  const { rules } = settings;
  if (!settings.captureEnabled) return skip('switched-off');
  if (!rules.enabled) return skip('disabled-in-app');
  // Our own fallback downloads (and other extensions') are never taken over.
  if (item.byExtensionId) return skip('started-by-extension');
  // Cookies of an incognito window must not leave it.
  if (item.incognito) return skip('incognito');
  if (item.state && item.state !== 'in_progress') return skip('not-in-progress');

  const url = parseHttpUrl(item.finalUrl || item.url);
  if (!url) return skip('not-http'); // blob:, data:, file:, ftp: stay with Chrome
  if (bypassed) return skip('shift-click');

  const referrerHost = parseHttpUrl(item.referrer)?.hostname;
  if (isExcluded(url.hostname, rules.excludedDomains) || (referrerHost && isExcluded(referrerHost, rules.excludedDomains))) {
    return skip('excluded-domain');
  }

  const size = knownSize(item);
  if (size !== undefined && size < rules.minSizeBytes) return skip('too-small');

  if (rules.extensions.length > 0) {
    const ext = fileExtension(item);
    if (!ext || !rules.extensions.some((e) => e.toLowerCase().replace(/^\./, '') === ext)) {
      return skip('file-type');
    }
  }

  return { capture: true };
}

export function knownSize(item: DownloadFacts): number | undefined {
  if (item.totalBytes && item.totalBytes > 0) return item.totalBytes;
  if (item.fileSize && item.fileSize > 0) return item.fileSize;
  return undefined;
}

/** True when host is the domain or a subdomain of it. */
export function isExcluded(host: string, domains: readonly string[]): boolean {
  const h = host.toLowerCase().replace(/\.$/, '');
  return domains.some((raw) => {
    const d = raw.trim().toLowerCase().replace(/^\*?\./, '').replace(/\.$/, '');
    return d.length > 0 && (h === d || h.endsWith('.' + d));
  });
}

const MIME_EXTENSIONS: Record<string, string> = {
  'application/zip': 'zip',
  'application/x-zip-compressed': 'zip',
  'application/x-7z-compressed': '7z',
  'application/vnd.rar': 'rar',
  'application/x-rar-compressed': 'rar',
  'application/gzip': 'gz',
  'application/x-msdownload': 'exe',
  'application/x-msi': 'msi',
  'application/x-ms-installer': 'msi',
  'application/x-iso9660-image': 'iso',
  'application/pdf': 'pdf',
  'application/vnd.android.package-archive': 'apk',
  'video/mp4': 'mp4',
  'video/webm': 'webm',
  'video/x-matroska': 'mkv',
  'audio/mpeg': 'mp3',
  'audio/flac': 'flac',
};

/** From the file name Chrome chose, else the URL path, else the MIME type (download?id=… links). */
export function fileExtension(item: DownloadFacts): string | undefined {
  const fromName = (name: string | undefined) => {
    const base = (name ?? '').split(/[\\/]/).pop() ?? '';
    const dot = base.lastIndexOf('.');
    return dot > 0 && dot < base.length - 1 ? base.slice(dot + 1).toLowerCase() : undefined;
  };
  const path = (u?: string) => {
    const parsed = parseHttpUrl(u);
    return parsed ? safeDecode(parsed.pathname) : undefined;
  };
  return fromName(item.filename) ?? fromName(path(item.finalUrl)) ?? fromName(path(item.url)) ?? MIME_EXTENSIONS[(item.mime ?? '').toLowerCase().split(';')[0].trim()];
}

export function parseHttpUrl(value: string | undefined): URL | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url : undefined;
  } catch {
    return undefined;
  }
}

function safeDecode(path: string): string {
  try {
    return decodeURIComponent(path);
  } catch {
    return path;
  }
}
