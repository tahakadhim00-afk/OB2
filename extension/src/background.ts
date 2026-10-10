// MV3 service worker: wires Chrome events to the capture logic. Listeners are registered at the
// top level so Chrome can wake the worker for them.

import { BypassList, captureDownload, downloadLinkWithOb2, refreshRules } from './capture';

const MENU_ID = 'ob2-download-link';
const bypass = new BypassList();

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: MENU_ID, title: 'Download with OB2', contexts: ['link'] }, () => void chrome.runtime.lastError);
  void refreshRules();
});

chrome.runtime.onStartup.addListener(() => void refreshRules());

chrome.downloads.onCreated.addListener((item) => {
  void captureDownload(item, bypass);
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_ID && info.linkUrl) {
    void downloadLinkWithOb2(info.linkUrl, info.pageUrl ?? tab?.url);
  }
});

// §8.4: the content script reports links clicked with Shift held.
chrome.runtime.onMessage.addListener((message: unknown, sender) => {
  const m = message as { type?: string; url?: unknown } | undefined;
  if (sender.id === chrome.runtime.id && m?.type === 'ob2-bypass' && typeof m.url === 'string') {
    bypass.add(m.url);
  }
  return false;
});
