// Runs in every frame of http(s) pages.
// §8.4: holding Shift while clicking a link lets Chrome download it instead of OB2. Reports only
// the clicked link's address, only when Shift is held; reads nothing else.
// FR-24: the "Download with OB2" button over videos (overlay.ts). The background tells this frame
// which media files it loaded; nothing about the page is sent anywhere until the button is clicked.
// D-72: a click on a video page (YouTube) asks OB2 for the page's qualities (listFormats).
// D-79: with the streams this frame's player loaded, which OB2 reads when yt-dlp doesn't know the site.
// D-70: the popup's "Find downloads" reads the page's links through scanPage (scan.ts).

import type { CaptureOutcome, FormatsOutcome } from './capture';
import { isMediaCandidate, isRequestHeaders, isStreamAddress, type MediaCandidate, type StreamInfo } from './media';
import { MediaOverlay, type SendResult } from './overlay';
import { loadButtonPosition, loadMediaButtonEnabled, MEDIA_BUTTON_KEY, saveButtonPosition } from './rules';
import { SCAN_GLOBAL, scanPage, type PageScan } from './scan';

function shiftClickedLink(event: MouseEvent): string | undefined {
  if (!event.shiftKey) return undefined;
  // composedPath reaches links inside shadow roots; <a> covers both HTML and SVG links.
  for (const node of event.composedPath()) {
    if (node instanceof Element && node.localName === 'a') {
      const href = node.getAttribute('href') ?? node.getAttributeNS('http://www.w3.org/1999/xlink', 'href');
      if (!href) return undefined;
      try {
        return new URL(href, node.baseURI).href;
      } catch {
        return undefined;
      }
    }
  }
  return undefined;
}

function onClick(event: MouseEvent): void {
  const url = shiftClickedLink(event);
  if (url) {
    chrome.runtime.sendMessage({ type: 'ob2-bypass', url }).catch(() => undefined);
  }
}

document.addEventListener('click', onClick, true);
document.addEventListener('auxclick', onClick, true);

export function sendResultOf(outcome: CaptureOutcome | undefined): SendResult {
  if (outcome?.action === 'captured') return 'sent';
  if (outcome?.action === 'left-to-chrome') return 'browser';
  if (outcome?.action === 'failed' && outcome.reason !== 'unknown') return 'unreachable';
  if (outcome?.action === 'ignored' && outcome.reason === 'incognito') return 'incognito';
  return 'failed';
}

async function sendMedia(media: MediaCandidate): Promise<SendResult> {
  // Throws when the extension was reloaded since this page loaded; the button then says so.
  return sendResultOf((await chrome.runtime.sendMessage({ type: 'ob2-download-media', media })) as CaptureOutcome | undefined);
}

/** D-72: asked only when the button is clicked on a video page; OB2 lists its qualities. */
async function listFormats(pageUrl: string, streams: readonly StreamInfo[]): Promise<FormatsOutcome> {
  try {
    const outcome = (await chrome.runtime.sendMessage({ type: 'ob2-media-formats', url: pageUrl, streams })) as FormatsOutcome | undefined;
    return outcome ?? { action: 'failed', reason: 'failed' };
  } catch {
    return { action: 'failed', reason: 'failed' }; // the extension was reloaded since this page loaded
  }
}

function startMediaButton(): void {
  // D-61: nothing from an Incognito window goes to OB2 (the background refuses it as well).
  if ((chrome.extension as { inIncognitoContext?: boolean } | undefined)?.inIncognitoContext) return;

  const overlay = new MediaOverlay(document, sendMedia, 'closed', listFormats);
  // D-70: the popup's "Find downloads". Lives in the extension's isolated world, out of the page's reach.
  (globalThis as Record<string, unknown>)[SCAN_GLOBAL] = (): PageScan => scanPage(document, overlay.detectedMedia());
  chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
    const m = message as { type?: string; media?: unknown; url?: unknown; headers?: unknown } | undefined;
    if (sender.id !== chrome.runtime.id) return false;
    if (m?.type === 'ob2-media' && isMediaCandidate(m.media)) {
      overlay.addDetected(m.media);
    } else if (m?.type === 'ob2-stream' && isStreamAddress(m.url)) {
      overlay.addStream(m.url, isRequestHeaders(m.headers) ? m.headers : {});
    } else if (m?.type === 'ob2-context-media') {
      sendResponse(overlay.mediaForContextMenu() ?? null);
    }
    return false;
  });

  loadMediaButtonEnabled()
    .then((enabled) => (overlay.enabled = enabled))
    .catch(() => undefined);
  // D-81: the button stays where the user dragged it on this site (in this frame's site, for a player in a frame).
  const site = location.hostname;
  loadButtonPosition(site)
    .then((position) => {
      if (position) overlay.position = position;
    })
    .catch(() => undefined);
  overlay.onMoved = (position) => void saveButtonPosition(site, position).catch(() => undefined);
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && MEDIA_BUTTON_KEY in changes) {
      overlay.enabled = changes[MEDIA_BUTTON_KEY].newValue !== false;
      if (!overlay.enabled) overlay.hide();
    }
  });
  overlay.attach();
}

startMediaButton();
