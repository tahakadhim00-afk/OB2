// Popup (§6 Phase 6 scope): connection state, active and recent downloads, Pause all / Resume all.
// Untrusted text (file names) is only ever set through textContent.

import { fetchStatus, type CaptureOutcome } from '../capture';
import { formatBytes } from '../format';
import { HostError, STATUS_TIMEOUT_MS, sendToHost, type HostFailure } from '../native';
import { createMessage, MessageType, type DownloadSummary, type StatusPayload } from '../protocol';
import { loadMediaButtonEnabled, loadSettings, setCaptureEnabled, setMediaButtonEnabled } from '../rules';
import { isPageScan, SCAN_GLOBAL, type FoundKind, type FoundLink, type PageScan } from '../scan';

export type View =
  | { kind: 'connected'; status: StatusPayload }
  | { kind: 'failed'; failure: HostFailure };

const FAILURE_TEXT: Record<HostFailure, { state: string; message: string }> = {
  'app-not-running': { state: 'Not running', message: 'OB2 is not running. It starts by itself when the browser hands it a download.' },
  'not-registered': { state: 'Not set up', message: 'The browser cannot find OB2. In OB2, open Options > Browser and click Register.' },
  forbidden: { state: 'Not set up', message: 'OB2 is registered for a different extension. In OB2, open Options > Browser and click Register.' },
  'host-exited': { state: 'Error', message: 'host.exe stopped unexpectedly. If antivirus blocked it, allow host.exe next to OB2.exe.' },
  timeout: { state: 'No answer', message: 'OB2 did not answer in time.' },
  'error-reply': { state: 'Error', message: 'OB2 reported an error.' },
  unknown: { state: 'Error', message: 'Could not talk to OB2.' },
};

export function render(doc: Document, view: View): void {
  const byId = <T extends HTMLElement>(id: string) => doc.getElementById(id) as T;
  const state = byId('state');
  const message = byId('message');
  const connected = view.kind === 'connected';

  byId<HTMLButtonElement>('pause-all').disabled = !connected;
  byId<HTMLButtonElement>('resume-all').disabled = !connected;

  if (view.kind === 'failed') {
    const text = FAILURE_TEXT[view.failure];
    state.textContent = text.state;
    state.className = view.failure === 'app-not-running' ? 'state warn' : 'state error';
    message.textContent = text.message;
    message.hidden = false;
    fillList(doc, 'active', []);
    fillList(doc, 'recent', []);
    return;
  }

  const { active, recent } = view.status;
  state.textContent = active.length === 0 ? 'Connected' : `${active.length} active · ${formatSpeed(active.reduce((s, d) => s + d.bytesPerSecond, 0))}`;
  state.className = 'state ok';
  message.hidden = true;
  fillList(doc, 'active', active);
  fillList(doc, 'recent', recent.slice(0, 5));
}

function fillList(doc: Document, id: 'active' | 'recent', items: DownloadSummary[]): void {
  const list = doc.getElementById(id)!;
  list.replaceChildren(
    ...items.map((d) => {
      const li = doc.createElement('li');
      const name = doc.createElement('span');
      name.className = 'name';
      name.textContent = d.fileName;
      name.title = d.fileName;
      li.append(name);
      if (id === 'active' && typeof d.progress === 'number') {
        const bar = doc.createElement('div');
        bar.className = 'bar';
        const fill = doc.createElement('span');
        fill.style.width = `${Math.max(0, Math.min(100, d.progress))}%`;
        bar.append(fill);
        bar.setAttribute('role', 'progressbar');
        bar.setAttribute('aria-valuenow', String(d.progress));
        bar.setAttribute('aria-valuemin', '0');
        bar.setAttribute('aria-valuemax', '100');
        li.append(bar);
      }
      const detail = doc.createElement('span');
      detail.className = 'detail';
      detail.textContent = describe(d, id === 'active');
      li.append(detail);
      return li;
    }),
  );
  doc.getElementById(`${id}-empty`)!.hidden = items.length > 0;
}

function describe(d: DownloadSummary, active: boolean): string {
  const parts: string[] = [];
  if (active) {
    if (typeof d.progress === 'number') parts.push(`${d.progress.toFixed(1)} %`);
    parts.push(formatSpeed(d.bytesPerSecond));
  } else {
    parts.push(d.status.charAt(0).toUpperCase() + d.status.slice(1));
  }
  if (d.totalBytes) parts.push(formatBytes(d.totalBytes));
  return parts.join(' · ');
}

export { formatBytes };

function formatSpeed(bytesPerSecond: number): string {
  return `${formatBytes(bytesPerSecond)}/s`;
}

// D-70: "Find downloads on this page". The list stays in the popup; a picked link goes through the
// background, which outlives the popup (it closes when OB2's window takes the focus).

export type FoundView =
  | { kind: 'scanning' }
  | { kind: 'failed'; message: string }
  | { kind: 'found'; tabId: number; scans: PageScan[] };

export type FoundSender = (tabId: number, link: FoundLink, pageUrl: string) => Promise<CaptureOutcome | undefined>;

const KIND_TEXT: Record<FoundKind, string> = { video: 'Video', audio: 'Audio', file: 'File', stream: 'Stream: not supported yet' };
const KIND_ORDER: FoundKind[] = ['video', 'audio', 'file', 'stream'];

/** One row per address across all frames, videos first, streams last. */
function mergeScans(scans: readonly PageScan[]): Array<{ link: FoundLink; pageUrl: string }> {
  const rows = new Map<string, { link: FoundLink; pageUrl: string }>();
  for (const scan of scans) {
    for (const link of scan.links) {
      if (!rows.has(link.url)) rows.set(link.url, { link, pageUrl: scan.pageUrl });
    }
  }
  return [...rows.values()].sort((a, b) => KIND_ORDER.indexOf(a.link.kind) - KIND_ORDER.indexOf(b.link.kind));
}

export function renderFound(doc: Document, view: FoundView, send: FoundSender = sendFound): void {
  const list = doc.getElementById('found')!;
  const message = doc.getElementById('scan-message')!;
  (doc.getElementById('scan') as HTMLButtonElement).disabled = view.kind === 'scanning';
  message.hidden = false;
  if (view.kind !== 'found') {
    list.replaceChildren();
    message.textContent = view.kind === 'scanning' ? 'Reading the page…' : view.message;
    return;
  }

  const rows = mergeScans(view.scans);
  message.textContent = rows.length === 0 ? 'No download links found on this page.' : `${rows.length} found`;
  list.replaceChildren(
    ...rows.map(({ link, pageUrl }) => {
      const li = doc.createElement('li');
      const text = doc.createElement('div');
      text.className = 'text';
      const name = doc.createElement('span');
      name.className = 'name';
      name.textContent = link.name;
      name.title = link.url;
      const detail = doc.createElement('span');
      detail.className = 'detail';
      detail.textContent = `${KIND_TEXT[link.kind]} · ${new URL(link.url).hostname}`;
      text.append(name, detail);
      const button = doc.createElement('button');
      button.type = 'button';
      button.textContent = 'Download';
      button.disabled = link.kind === 'stream';
      button.setAttribute('aria-label', `Download ${link.name} with OB2`);
      button.addEventListener('click', () => {
        button.disabled = true;
        button.textContent = 'Sending…';
        void send(view.tabId, link, pageUrl)
          .catch(() => undefined)
          .then((outcome) => (button.textContent = resultText(outcome)));
      });
      li.append(text, button);
      return li;
    }),
  );
}

function resultText(outcome: CaptureOutcome | undefined): string {
  if (outcome?.action === 'captured') return 'Sent';
  if (outcome?.action === 'left-to-chrome') return 'In browser';
  return 'Failed';
}

async function sendFound(tabId: number, link: FoundLink, pageUrl: string): Promise<CaptureOutcome | undefined> {
  return (await chrome.runtime.sendMessage({ type: 'ob2-download-found', tabId, link, pageUrl })) as CaptureOutcome | undefined;
}

/** Runs in each frame (serialized by chrome.scripting), so it may only use its argument. */
function callScan(name: string): unknown {
  const scan = (globalThis as Record<string, unknown>)[name];
  return typeof scan === 'function' ? (scan as () => unknown)() : null;
}

async function scanActiveTab(): Promise<FoundView> {
  const failed = (message: string): FoundView => ({ kind: 'failed', message });
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (tab?.id === undefined) return failed('There is no page to read.');
  if (tab.incognito) return failed('OB2 does not read Incognito pages.');
  let results: chrome.scripting.InjectionResult<unknown>[];
  try {
    results = await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: callScan, args: [SCAN_GLOBAL] });
  } catch {
    return failed("The browser doesn't let extensions read this page (for example chrome:// pages or the Web Store).");
  }
  const scans = results.map((r) => r.result).filter(isPageScan);
  // No answer from any frame: the page was open before the extension was installed or reloaded.
  return scans.length > 0 ? { kind: 'found', tabId: tab.id, scans } : failed('Reload the page (F5), then try again.');
}

let refreshing = false;

async function refresh(doc: Document): Promise<void> {
  if (refreshing) return; // a slow host must not pile up requests
  refreshing = true;
  try {
    render(doc, { kind: 'connected', status: await fetchStatus() });
  } catch (error) {
    render(doc, { kind: 'failed', failure: error instanceof HostError ? error.failure : 'unknown' });
  } finally {
    refreshing = false;
  }
}

async function command(doc: Document, type: string): Promise<void> {
  try {
    await sendToHost(createMessage(type), STATUS_TIMEOUT_MS);
  } catch {
    // The refresh below shows what went wrong.
  }
  await refresh(doc);
}

export async function start(doc: Document): Promise<void> {
  const capture = doc.getElementById('capture') as HTMLInputElement;
  capture.checked = (await loadSettings()).captureEnabled;
  capture.addEventListener('change', () => void setCaptureEnabled(capture.checked));
  const mediaButton = doc.getElementById('media-button') as HTMLInputElement;
  mediaButton.checked = await loadMediaButtonEnabled();
  mediaButton.addEventListener('change', () => void setMediaButtonEnabled(mediaButton.checked));
  doc.getElementById('scan')!.addEventListener('click', () => {
    renderFound(doc, { kind: 'scanning' });
    void scanActiveTab().then((view) => renderFound(doc, view));
  });
  doc.getElementById('pause-all')!.addEventListener('click', () => void command(doc, MessageType.PauseAll));
  doc.getElementById('resume-all')!.addEventListener('click', () => void command(doc, MessageType.ResumeAll));

  await refresh(doc);
  // Each refresh starts host.exe once; 2 s keeps the popup live without spawning too many.
  // The timer ends with the popup.
  setInterval(() => void refresh(doc), 2000);
}

if (typeof document !== 'undefined' && document.getElementById('capture')) {
  void start(document);
}
