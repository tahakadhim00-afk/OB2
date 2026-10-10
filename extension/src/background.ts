// MV3 service worker: wires Chrome events to the capture logic. Listeners are registered at the
// top level so Chrome can wake the worker for them.

import { BypassList, captureDownload, downloadLinkWithOb2, downloadMediaWithOb2, refreshRules } from './capture';
import { isDownloadableMediaUrl, isMediaCandidate, mediaFromResponse, type MediaCandidate } from './media';

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
    void downloadLinkWithOb2(info.linkUrl, info.pageUrl ?? tab?.url);
  } else if (info.menuItemId === MEDIA_MENU_ID) {
    void downloadMediaFromMenu(info, tab);
  }
});

// FR-24 (D-69): watch responses (read-only) for video and audio files and tell the frame that
// loaded them, so its button can offer them. The frame keeps the list; nothing is stored here.
chrome.webRequest.onResponseStarted.addListener(
  (details) => {
    if (details.tabId < 0) return; // not from a tab (the extension itself, a service worker)
    const media = mediaFromResponse(details);
    if (media) {
      chrome.tabs.sendMessage(details.tabId, { type: 'ob2-media', media }, { frameId: details.frameId }).catch(() => undefined);
    }
  },
  // <video>/<audio> loads, and players that fetch the file themselves (fetch and XHR).
  { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest'] },
  ['responseHeaders'],
);

chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return false;
  const m = message as { type?: string; url?: unknown; media?: unknown } | undefined;
  // §8.4: the content script reports links clicked with Shift held.
  if (m?.type === 'ob2-bypass' && typeof m.url === 'string') {
    bypass.add(m.url);
  } else if (m?.type === 'ob2-download-media' && sender.tab && isMediaCandidate(m.media)) {
    void downloadMediaWithOb2(m.media, sender.url ?? sender.tab.url, sender.tab.title, sender.tab.incognito).then(sendResponse);
    return true; // the reply comes after OB2 answers
  }
  return false;
});

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
