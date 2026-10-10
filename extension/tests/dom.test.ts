/**
 * @jest-environment jsdom
 */
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { formatBytes, render, renderFound } from '../src/popup/popup';

function loadPopup(): Document {
  const html = readFileSync(join(__dirname, '..', 'src', 'popup', 'popup.html'), 'utf8');
  document.documentElement.innerHTML = html.replace(/^<!doctype html>/i, '');
  return document;
}

const EXTENSION_ID = 'mgmmgplfngnhcencbhpfagnjcnenflpm';
const sendMessage = jest.fn().mockResolvedValue(undefined);
const onMessage = jest.fn();
type Listener = (message: unknown, sender: { id?: string }, reply: (r: unknown) => void) => boolean;
let content: typeof import('../src/content');
let listener: Listener;

beforeAll(async () => {
  (globalThis as unknown as { chrome: unknown }).chrome = {
    runtime: { id: EXTENSION_ID, sendMessage, onMessage: { addListener: onMessage } },
    storage: { local: { get: jest.fn().mockResolvedValue({}) }, onChanged: { addListener: jest.fn() } },
  };
  content = await import('../src/content');
  listener = onMessage.mock.calls[0][0] as Listener;
});

describe('content script: Shift-click bypass (§8.4)', () => {

  beforeEach(() => {
    sendMessage.mockClear();
    document.body.innerHTML = '<a id="link" href="https://example.com/big.zip"><span id="inner">Download</span></a><p id="text">x</p>';
  });

  const click = (id: string, init: MouseEventInit) =>
    document.getElementById(id)!.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, composed: true, ...init }));

  it('reports a link clicked with Shift held, even from a child element', () => {
    click('inner', { shiftKey: true });

    expect(sendMessage).toHaveBeenCalledWith({ type: 'ob2-bypass', url: 'https://example.com/big.zip' });
  });

  it('stays silent for normal clicks and for clicks outside links', () => {
    click('link', {});
    click('text', { shiftKey: true });

    expect(sendMessage).not.toHaveBeenCalled();
  });
});

describe('content script: media from the background (FR-24)', () => {
  it('remembers media the frame loaded and hands it to the context menu', () => {
    document.body.innerHTML = '';
    const reply = jest.fn();
    listener({ type: 'ob2-media', media: { url: 'https://cdn.e.com/a.mp4', size: 9_000_000 } }, { id: EXTENSION_ID }, jest.fn());

    listener({ type: 'ob2-context-media' }, { id: EXTENSION_ID }, reply);

    expect(reply).toHaveBeenCalledWith({ url: 'https://cdn.e.com/a.mp4', size: 9_000_000 });
  });

  it('ignores other senders and malformed media', () => {
    const reply = jest.fn();
    listener({ type: 'ob2-media', media: { url: 'javascript:alert(1)' } }, { id: EXTENSION_ID }, jest.fn());
    listener({ type: 'ob2-context-media' }, { id: 'someone-else' }, reply);

    expect(reply).not.toHaveBeenCalled();
  });

  it.each([
    [{ action: 'captured', status: 'queued' }, 'sent'],
    [{ action: 'left-to-chrome', reason: 'not-registered' }, 'browser'],
    [{ action: 'ignored', reason: 'incognito' }, 'incognito'],
    [undefined, 'failed'],
  ] as const)('turns the outcome %j into "%s" on the button', (outcome, result) => {
    expect(content.sendResultOf(outcome)).toBe(result);
  });
});

describe('popup', () => {
  it('shows active downloads, progress and total speed', () => {
    const doc = loadPopup();
    render(doc, {
      kind: 'connected',
      status: {
        active: [{ id: 1, fileName: 'ubuntu.iso', status: 'downloading', progress: 42.5, bytesPerSecond: 2 * 1024 * 1024, totalBytes: 5 * 1024 ** 3 }],
        recent: [{ id: 2, fileName: 'notes.pdf', status: 'completed', progress: 100, bytesPerSecond: 0, totalBytes: 2048 }],
        captureRules: { enabled: true, minSizeBytes: 1, extensions: [], excludedDomains: [] },
      },
    });

    expect(doc.getElementById('state')!.textContent).toBe('1 active · 2.0 MB/s');
    expect(doc.querySelector('#active .name')!.textContent).toBe('ubuntu.iso');
    expect((doc.querySelector('#active .bar > span') as HTMLElement).style.width).toBe('42.5%');
    expect(doc.querySelector('#recent .detail')!.textContent).toBe('Completed · 2.0 KB');
    expect(doc.getElementById('active-empty')!.hidden).toBe(true);
    expect((doc.getElementById('pause-all') as HTMLButtonElement).disabled).toBe(false);
  });

  it('never interprets file names as HTML', () => {
    const doc = loadPopup();
    render(doc, {
      kind: 'connected',
      status: { active: [], recent: [{ id: 1, fileName: '<img src=x onerror=alert(1)>.zip', status: 'failed', bytesPerSecond: 0 }], captureRules: { enabled: true, minSizeBytes: 1, extensions: [], excludedDomains: [] } },
    });

    expect(doc.querySelector('#recent img')).toBeNull();
    expect(doc.querySelector('#recent .name')!.textContent).toBe('<img src=x onerror=alert(1)>.zip');
  });

  it('explains how to set up a missing registration and disables the buttons', () => {
    const doc = loadPopup();
    render(doc, { kind: 'failed', failure: 'not-registered' });

    expect(doc.getElementById('state')!.textContent).toBe('Not set up');
    expect(doc.getElementById('message')!.textContent).toContain('Options > Browser');
    expect((doc.getElementById('resume-all') as HTMLButtonElement).disabled).toBe(true);
  });

  it('says OB2 will start by itself when it is not running', () => {
    const doc = loadPopup();
    render(doc, { kind: 'failed', failure: 'app-not-running' });

    expect(doc.getElementById('message')!.textContent).toContain('starts by itself');
  });

  describe('Find downloads on this page (D-70)', () => {
    const scans = [
      { pageUrl: 'https://e.com/', title: 'E', links: [
        { url: 'https://e.com/setup.exe', name: 'setup.exe', kind: 'file' as const },
        { url: 'https://e.com/live.m3u8', name: 'live.m3u8', kind: 'stream' as const },
      ] },
      { pageUrl: 'https://player.e.com/embed', title: 'Player', links: [
        { url: 'https://cdn.e.com/movie.mp4', name: '<b>movie</b>.mp4', kind: 'video' as const },
        { url: 'https://e.com/setup.exe', name: 'setup.exe', kind: 'file' as const },
      ] },
    ];

    it('lists each address once, videos first and streams last, which cannot be downloaded', () => {
      const doc = loadPopup();
      render(doc, { kind: 'failed', failure: 'app-not-running' });
      renderFound(doc, { kind: 'found', tabId: 4, scans });

      const rows = Array.from(doc.querySelectorAll('#found li'));
      expect(doc.getElementById('scan-message')!.textContent).toBe('3 found');
      expect(rows.map((r) => r.querySelector('.name')!.textContent)).toEqual(['<b>movie</b>.mp4', 'setup.exe', 'live.m3u8']);
      expect(doc.querySelector('#found b')).toBeNull();
      expect(rows[2].querySelector('.detail')!.textContent).toBe('Stream: not supported yet · e.com');
      expect(rows[2].querySelector('button')!.disabled).toBe(true);
    });

    it('sends a picked link with the frame it came from, and shows the result', async () => {
      const doc = loadPopup();
      const send = jest.fn().mockResolvedValue({ action: 'captured', status: 'confirming' });
      renderFound(doc, { kind: 'found', tabId: 4, scans }, send);

      const button = doc.querySelector('#found li button') as HTMLButtonElement;
      button.click();
      expect(button.textContent).toBe('Sending…');
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect(send).toHaveBeenCalledWith(4, scans[1].links[0], 'https://player.e.com/embed');
      expect(button.textContent).toBe('Sent');
    });

    it('explains an empty page or a page it cannot read', () => {
      const doc = loadPopup();
      renderFound(doc, { kind: 'found', tabId: 1, scans: [{ pageUrl: 'https://e.com/', title: '', links: [] }] });
      expect(doc.getElementById('scan-message')!.textContent).toBe('No download links found on this page.');

      renderFound(doc, { kind: 'failed', message: 'Reload the page (F5), then try again.' });
      expect(doc.getElementById('found')!.children).toHaveLength(0);
      expect(doc.getElementById('scan-message')!.textContent).toContain('Reload the page');
    });
  });

  it.each([
    [0, '0 B'],
    [1023, '1023 B'],
    [1536, '1.5 KB'],
    [10 * 1024 * 1024, '10 MB'],
    [5 * 1024 ** 3, '5.0 GB'],
  ])('formats %d bytes as %s', (bytes, text) => {
    expect(formatBytes(bytes)).toBe(text);
  });
});
