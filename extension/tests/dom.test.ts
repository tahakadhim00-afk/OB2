/**
 * @jest-environment jsdom
 */
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { formatBytes, render } from '../src/popup/popup';

function loadPopup(): Document {
  const html = readFileSync(join(__dirname, '..', 'src', 'popup', 'popup.html'), 'utf8');
  document.documentElement.innerHTML = html.replace(/^<!doctype html>/i, '');
  return document;
}

describe('content script: Shift-click bypass (§8.4)', () => {
  const sendMessage = jest.fn().mockResolvedValue(undefined);

  beforeAll(async () => {
    (globalThis as unknown as { chrome: unknown }).chrome = { runtime: { sendMessage } };
    await import('../src/content');
  });

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
