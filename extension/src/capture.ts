// Browser download capture (§7.4, §8.4).
// The browser download is paused, not cancelled, while OB2 is asked to take it. It is cancelled
// only after OB2 acknowledges; on any failure it is resumed, so Chrome finishes it (fail-open).

import { mediaFileName, type MediaCandidate } from './media';
import { CAPTURE_TIMEOUT_MS, HostError, STATUS_TIMEOUT_MS, sendToHost } from './native';
import { createMessage, MessageType, type AckPayload, type AddDownloadPayload, type StatusPayload } from './protocol';
import { decide, knownSize, loadSettings, RULES_MAX_AGE_MS, saveRules, type SkipReason } from './rules';

/** Links clicked with Shift held, remembered briefly so the download they start is left to Chrome. */
export class BypassList {
  static readonly TTL_MS = 15_000;
  private readonly entries = new Map<string, number>();

  add(url: string, now = Date.now()): void {
    this.prune(now);
    this.entries.set(url, now + BypassList.TTL_MS);
  }

  /** True (and forgets the entry) when one of the urls was Shift-clicked within the TTL. */
  consume(urls: Array<string | undefined>, now = Date.now()): boolean {
    this.prune(now);
    for (const url of urls) {
      if (url && this.entries.delete(url)) {
        return true;
      }
    }
    return false;
  }

  private prune(now: number): void {
    for (const [url, expires] of this.entries) {
      if (expires <= now) this.entries.delete(url);
    }
  }
}

export type CaptureOutcome =
  | { action: 'ignored'; reason: SkipReason | 'pause-failed' }
  | { action: 'captured'; downloadId?: number | null; status: string }
  | { action: 'left-to-chrome'; reason: string };

export async function captureDownload(item: chrome.downloads.DownloadItem, bypass: BypassList): Promise<CaptureOutcome> {
  const settings = await loadSettings();
  if (Date.now() - settings.rulesFetchedAt > RULES_MAX_AGE_MS) {
    void refreshRules(); // keep the browser waiting as little as possible; these rules apply next time
  }

  const decision = decide(item, settings, bypass.consume([item.url, item.finalUrl]));
  if (!decision.capture) {
    return { action: 'ignored', reason: decision.reason };
  }

  try {
    await chrome.downloads.pause(item.id);
  } catch {
    // Already finished or cancelled: nothing to take over.
    return { action: 'ignored', reason: 'pause-failed' };
  }

  try {
    const payload = await buildPayload(item, 'chrome-downloads');
    const reply = await sendToHost<AckPayload>(createMessage(MessageType.AddDownload, payload), CAPTURE_TIMEOUT_MS);
    if (reply.type !== MessageType.Ack) {
      throw new HostError('unknown', `Unexpected reply "${reply.type}".`);
    }

    await quietly(chrome.downloads.cancel(item.id));
    await quietly(chrome.downloads.erase({ id: item.id }));
    return { action: 'captured', downloadId: reply.payload?.downloadId, status: reply.payload?.status ?? 'queued' };
  } catch (error) {
    await quietly(chrome.downloads.resume(item.id));
    return { action: 'left-to-chrome', reason: error instanceof HostError ? error.failure : 'unknown' };
  }
}

/** What the page knows about a link that Chrome hasn't started downloading. */
export interface LinkHints {
  source?: AddDownloadPayload['source'];
  filename?: string;
  mime?: string;
  size?: number;
}

/** FR-21: "Download with OB2" on a link. If OB2 can't be reached, Chrome downloads it instead. */
export async function downloadLinkWithOb2(linkUrl: string, pageUrl: string | undefined, hints: LinkHints = {}): Promise<CaptureOutcome> {
  try {
    const item = { url: linkUrl, referrer: pageUrl, filename: hints.filename, mime: hints.mime, totalBytes: hints.size };
    const payload = await buildPayload(item, hints.source ?? 'chrome-context-menu');
    const reply = await sendToHost<AckPayload>(createMessage(MessageType.AddDownload, payload), CAPTURE_TIMEOUT_MS);
    return { action: 'captured', downloadId: reply.payload?.downloadId, status: reply.payload?.status ?? 'queued' };
  } catch (error) {
    // A download started by this extension is never captured again (byExtensionId).
    await quietly(chrome.downloads.download({ url: linkUrl }));
    return { action: 'left-to-chrome', reason: error instanceof HostError ? error.failure : 'unknown' };
  }
}

/**
 * FR-24: a video or audio file from the button over a video or the media context menu. Named
 * after the page when its address has no file name. Same fail-open rule as a link.
 */
export async function downloadMediaWithOb2(media: MediaCandidate, pageUrl: string | undefined, pageTitle: string | undefined, incognito = false): Promise<CaptureOutcome> {
  if (incognito) return { action: 'ignored', reason: 'incognito' }; // D-61
  return downloadLinkWithOb2(media.url, pageUrl, { source: 'chrome-media', filename: mediaFileName(media, pageTitle), mime: media.mime, size: media.size });
}

type PayloadSource =Pick<chrome.downloads.DownloadItem, 'url'> & Partial<Pick<chrome.downloads.DownloadItem, 'finalUrl' | 'referrer' | 'filename' | 'mime' | 'totalBytes' | 'fileSize'>>;

export async function buildPayload(item: PayloadSource, source: AddDownloadPayload['source']): Promise<AddDownloadPayload> {
  const target = item.finalUrl || item.url;
  const payload: AddDownloadPayload = { url: item.url, source, autoStart: false, userAgent: navigator.userAgent };
  if (item.finalUrl && item.finalUrl !== item.url) payload.finalUrl = item.finalUrl;
  if (item.referrer) payload.referrer = item.referrer;
  const name = (item.filename ?? '').split(/[\\/]/).pop();
  if (name) payload.filenameHint = name;
  if (item.mime) payload.mimeType = item.mime;
  const size = knownSize(item);
  if (size !== undefined) payload.fileSizeHint = size;
  const cookies = await cookieHeader(target);
  if (cookies) payload.cookies = cookies;
  const languages = navigator.languages?.join(',');
  if (languages) payload.headers = { 'Accept-Language': languages };
  return payload;
}

/** The Cookie header Chrome would send to this URL (§8.1 cookies permission, AC-5). */
export async function cookieHeader(url: string): Promise<string | undefined> {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.length > 0 ? cookies.map((c) => `${c.name}=${c.value}`).join('; ') : undefined;
  } catch {
    return undefined;
  }
}

/** Asks the app for status; caches the capture rules that come with it. */
export async function fetchStatus(): Promise<StatusPayload> {
  const reply = await sendToHost<StatusPayload>(createMessage(MessageType.GetStatus), STATUS_TIMEOUT_MS);
  if (reply.type !== MessageType.Status || !reply.payload) {
    throw new HostError('unknown', `Unexpected reply "${reply.type}".`);
  }
  await saveRules(reply.payload.captureRules);
  return reply.payload;
}

export async function refreshRules(): Promise<void> {
  try {
    await fetchStatus();
  } catch {
    // App not running: keep the cached (or default) rules. get_status never starts the app.
  }
}

async function quietly(promise: Promise<unknown>): Promise<void> {
  try {
    await promise;
  } catch {
    // The download may already be gone; nothing else to do.
  }
}
