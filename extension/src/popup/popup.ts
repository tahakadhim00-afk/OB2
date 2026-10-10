// Popup (§6 Phase 6 scope): connection state, active and recent downloads, Pause all / Resume all.
// Untrusted text (file names) is only ever set through textContent.

import { fetchStatus } from '../capture';
import { HostError, STATUS_TIMEOUT_MS, sendToHost, type HostFailure } from '../native';
import { createMessage, MessageType, type DownloadSummary, type StatusPayload } from '../protocol';
import { loadSettings, setCaptureEnabled } from '../rules';

export type View =
  | { kind: 'connected'; status: StatusPayload }
  | { kind: 'failed'; failure: HostFailure };

const FAILURE_TEXT: Record<HostFailure, { state: string; message: string }> = {
  'app-not-running': { state: 'Not running', message: 'OB2 is not running. It starts by itself when Chrome hands it a download.' },
  'not-registered': { state: 'Not set up', message: 'Chrome cannot find OB2. In OB2, open Options > Browser and click Register.' },
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

export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

export function formatSpeed(bytesPerSecond: number): string {
  return `${formatBytes(bytesPerSecond)}/s`;
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
