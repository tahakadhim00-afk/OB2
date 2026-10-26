// MV3 service worker: wires Chrome events to the capture logic. Listeners are registered at the
// top level so Chrome can wake the worker for them.

import {
  BypassList, captureDownload, downloadFoundWithOb2, downloadLinkWithOb2, downloadMediaWithOb2, downloadVideoPageWithOb2, fetchMediaFormats, MAX_STREAMS, refreshRules,
  type CaptureOutcome,
} from './capture';
import {
  isDownloadableMediaUrl, isMediaCandidate, isStreamInfo, mediaFromResponse, requestHeadersOf, streamFromResponse, youTubeVideoPage, type MediaCandidate, type RequestHeaders,
} from './media';
import { parseHttpUrl } from './rules';
import { isFoundLink, type FoundLink } from './scan';

const LINK_MENU_ID = 'ob2-download-link';
const MEDIA_MENU_ID = 'ob2-download-media';
const bypass = new BypassList();

chrome.runtime.onInstalled.addListener(() => {
  // Also runs on update, so menus added by a newer version appear without a reinstall.
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: LINK_MENU_ID, title: 'Download with OB2', contexts: ['link'] }, () => void chrome.runtime.lastError);
    chrome.contextMenus.create({ id: MEDIA_MENU_ID, title: 'Download media with OB2', contexts: ['video', 'audio'] }, () => void chrome.runtime.lastError);
  });
  void refreshRules();
});

chrome.runtime.onStartup.addListener(() => void refreshRules());

chrome.downloads.onCreated.addListener((item) => {
  void captureDownload(item, bypass);
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === LINK_MENU_ID && info.linkUrl) {
    // D-71: a link to a YouTube video opens OB2's quality chooser instead of saving the web page.
    const video = youTubeVideoPage(info.linkUrl);
    if (video && !tab?.incognito) void downloadVideoPageWithOb2(video, info.pageUrl ?? tab?.url);
    else if (!video) void downloadLinkWithOb2(info.linkUrl, info.pageUrl ?? tab?.url);
  } else if (info.menuItemId === MEDIA_MENU_ID) {
    void downloadMediaFromMenu(info, tab);
  }
});

// <video>/<audio> loads, and players that fetch the file themselves (fetch and XHR).
const PLAYER_REQUESTS: chrome.webRequest.RequestFilter = { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest'] };

// D-80: the headers Chrome sent with each such request, until its response shows whether it was
// a stream. Players' servers check them (a token header of the player's, Origin, Referer), so OB2
// sends them again. Kept in memory only, and briefly; a stream's go to its frame.
const SENT_HEADERS_KEPT = 500;
const sentHeaders = new Map<string, RequestHeaders>();
chrome.webRequest.onSendHeaders.addListener(
  (details) => {
    if (details.tabId < 0) return;
    sentHeaders.delete(details.requestId); // a redirect sends again under the same id
    sentHeaders.set(details.requestId, requestHeadersOf(details.requestHeaders));
    if (sentHeaders.size > SENT_HEADERS_KEPT) sentHeaders.delete(sentHeaders.keys().next().value!);
  },
  PLAYER_REQUESTS,
  // Chrome hides Referer and Origin without extraHeaders.
  ['requestHeaders', 'extraHeaders'],
);
chrome.webRequest.onErrorOccurred.addListener((details) => void sentHeaders.delete(details.requestId), PLAYER_REQUESTS);

// FR-24 (D-69): watch responses (read-only) for video and audio files and tell the frame that
// loaded them, so its button can offer them. D-79: likewise the HLS and DASH manifests, which
// go to OB2 only with a click on the button. The frame keeps the lists; nothing is stored here.
chrome.webRequest.onResponseStarted.addListener(
  (details) => {
    const headers = sentHeaders.get(details.requestId) ?? {};
    sentHeaders.delete(details.requestId);
    if (details.tabId < 0) return; // not from a tab (the extension itself, a service worker)
    const media = mediaFromResponse(details);
    const stream = media ? undefined : streamFromResponse(details);
    if (media) {
      chrome.tabs.sendMessage(details.tabId, { type: 'ob2-media', media }, { frameId: details.frameId }).catch(() => undefined);
    } else if (stream) {
      chrome.tabs.sendMessage(details.tabId, { type: 'ob2-stream', url: stream, headers }, { frameId: details.frameId }).catch(() => undefined);
    }
  },
  PLAYER_REQUESTS,
  ['responseHeaders'],
);

chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return false;
  const m = message as { type?: string; url?: unknown; media?: unknown; link?: unknown; tabId?: unknown; pageUrl?: unknown; streams?: unknown } | undefined;
  // §8.4: the content script reports links clicked with Shift held.
  if (m?.type === 'ob2-bypass' && typeof m.url === 'string') {
    bypass.add(m.url);
  } else if (m?.type === 'ob2-download-media' && sender.tab && isMediaCandidate(m.media)) {
    void downloadMediaWithOb2(m.media, sender.url ?? sender.tab.url, sender.tab.title, sender.tab.incognito).then(sendResponse);
    return true; // the reply comes after OB2 answers
  } else if (m?.type === 'ob2-media-formats' && sender.tab && typeof m.url === 'string' && parseHttpUrl(m.url)) {
    // D-72: the button's quality menu, after a click on it. D-79: with the streams the frame's
    // player loaded, and the tab's title (an embedded player's frame has none worth using).
    const streams = Array.isArray(m.streams) ? m.streams.filter(isStreamInfo).slice(0, MAX_STREAMS) : [];
    void fetchMediaFormats(m.url, sender.tab.incognito, streams, sender.tab.title).then(sendResponse);
    return true;
  } else if (m?.type === 'ob2-download-found' && !sender.tab && isFoundLink(m.link) && typeof m.tabId === 'number') {
    // From the popup's list (D-70). Handled here because the popup closes when OB2's window opens.
    void downloadFound(m.tabId, m.link, typeof m.pageUrl === 'string' ? m.pageUrl : undefined).then(sendResponse);
    return true;
  }
  return false;
});

async function downloadFound(tabId: number, link: FoundLink, pageUrl: string | undefined): Promise<CaptureOutcome> {
  const tab = await chrome.tabs.get(tabId).catch(() => undefined);
  return downloadFoundWithOb2(link, pageUrl ?? tab?.url, tab?.incognito ?? false);
}

/** Chrome gives the element's src; a blob: src is resolved by the frame from what it loaded. */
async function downloadMediaFromMenu(info: chrome.contextMenus.OnClickData, tab: chrome.tabs.Tab | undefined): Promise<void> {
  if (!tab || tab.id === undefined || tab.incognito) return;
  let media: MediaCandidate | undefined;
  if (isDownloadableMediaUrl(info.srcUrl)) {
    media = { url: info.srcUrl! };
  } else {
    try {
      const reply: unknown = await chrome.tabs.sendMessage(tab.id, { type: 'ob2-context-media' }, { frameId: info.frameId ?? 0 });
      media = isMediaCandidate(reply) ? reply : undefined;
    } catch {
      // The frame has no content script (chrome:// pages, or loaded before the extension).
    }
  }
  if (media) {
    await downloadMediaWithOb2(media, info.frameUrl ?? info.pageUrl ?? tab.url, tab.title);
  }
}
