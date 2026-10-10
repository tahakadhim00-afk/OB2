import { BypassList, buildPayload, captureDownload, cookieHeader, downloadLinkWithOb2, downloadMediaWithOb2, fetchStatus } from '../src/capture';
import { loadSettings } from '../src/rules';
import { download, installChrome, replyWith, type ChromeMock } from './chromeMock';

let chromeMock: ChromeMock;

beforeEach(() => {
  chromeMock = installChrome();
  // Rules are fresh, so no background get_status muddies the call counts.
  chromeMock.storage.rulesFetchedAt = Date.now();
});

const sentMessages = () => chromeMock.sendNativeMessage.mock.calls.map((c) => c[1] as { type: string; payload: Record<string, unknown> });

describe('capture flow (§7.4, §8.4)', () => {
  it('pauses, hands the download to OB2, and cancels and erases it after the ack', async () => {
    chromeMock.sendNativeMessage.mockImplementation(replyWith('ack', { downloadId: 42, status: 'queued' }));

    const outcome = await captureDownload(download(), new BypassList());

    expect(outcome).toEqual({ action: 'captured', downloadId: 42, status: 'queued' });
    expect(chromeMock.downloads.pause).toHaveBeenCalledWith(7);
    expect(chromeMock.downloads.cancel).toHaveBeenCalledWith(7);
    expect(chromeMock.downloads.erase).toHaveBeenCalledWith({ id: 7 });
    expect(chromeMock.downloads.resume).not.toHaveBeenCalled();
    // The browser download is cancelled only after OB2 answered.
    expect(chromeMock.downloads.cancel.mock.invocationCallOrder[0]).toBeGreaterThan(chromeMock.sendNativeMessage.mock.invocationCallOrder[0]);
  });

  it('sends the envelope and the payload from §8.3', async () => {
    chromeMock.cookies.getAll.mockResolvedValue([{ name: 'session', value: 'abc123' }, { name: 'other', value: 'xyz' }]);
    chromeMock.sendNativeMessage.mockImplementation(replyWith('ack', { downloadId: 1, status: 'queued' }));

    await captureDownload(download({ filename: 'C:\\Users\\me\\Downloads\\setup.exe' }), new BypassList());

    const [host, message] = chromeMock.sendNativeMessage.mock.calls[0];
    expect(host).toBe('com.ob2.host');
    expect(message).toMatchObject({ v: 1, type: 'add_download' });
    expect(message.id).toMatch(/^[0-9a-f-]{36}$/);
    expect(message.payload).toEqual({
      url: 'https://example.com/files/setup.exe',
      finalUrl: 'https://cdn.example.com/abc/setup.exe',
      referrer: 'https://example.com/downloads',
      filenameHint: 'setup.exe',
      mimeType: 'application/octet-stream',
      fileSizeHint: 524_288_000,
      userAgent: navigator.userAgent,
      cookies: 'session=abc123; other=xyz',
      headers: expect.any(Object),
      source: 'chrome-downloads',
      autoStart: false,
    });
    // Cookies belong to the host OB2 will actually download from.
    expect(chromeMock.cookies.getAll).toHaveBeenCalledWith({ url: 'https://cdn.example.com/abc/setup.exe' });
  });

  it.each([
    ['the host is not registered', () => Promise.reject(new Error('Specified native messaging host not found.')), 'not-registered'],
    ['host.exe exits', () => Promise.reject(new Error('Native host has exited.')), 'host-exited'],
    ['the app reports an error', replyWith('error', { code: 'INTERNAL_ERROR', message: 'x' }), 'error-reply'],
    ['the app is unreachable', replyWith('error', { code: 'PIPE_TIMEOUT', message: 'x' }), 'error-reply'],
    ['the reply is empty', () => Promise.resolve(undefined), 'unknown'],
    ['the reply is not an ack', replyWith('pong', { appRunning: true }), 'unknown'],
  ])('fails open when %s: the browser download resumes', async (_name, behaviour, reason) => {
    chromeMock.sendNativeMessage.mockImplementation(behaviour);

    const outcome = await captureDownload(download(), new BypassList());

    expect(outcome).toEqual({ action: 'left-to-chrome', reason });
    expect(chromeMock.downloads.resume).toHaveBeenCalledWith(7);
    expect(chromeMock.downloads.cancel).not.toHaveBeenCalled();
  });

  it('fails open when OB2 does not answer in time', async () => {
    jest.useFakeTimers();
    try {
      chromeMock.sendNativeMessage.mockReturnValue(new Promise(() => undefined));
      const pending = captureDownload(download(), new BypassList());
      await jest.advanceTimersByTimeAsync(20_001);

      expect(await pending).toEqual({ action: 'left-to-chrome', reason: 'timeout' });
      expect(chromeMock.downloads.resume).toHaveBeenCalledWith(7);
    } finally {
      jest.useRealTimers();
    }
  });

  it('does nothing when the download cannot be paused (already finished)', async () => {
    chromeMock.downloads.pause.mockRejectedValue(new Error('Download must be in progress'));

    expect(await captureDownload(download(), new BypassList())).toEqual({ action: 'ignored', reason: 'pause-failed' });
    expect(chromeMock.sendNativeMessage).not.toHaveBeenCalled();
  });

  it('leaves skipped downloads completely alone', async () => {
    const outcome = await captureDownload(download({ totalBytes: 1000, fileSize: 1000 }), new BypassList());

    expect(outcome).toEqual({ action: 'ignored', reason: 'too-small' });
    expect(chromeMock.downloads.pause).not.toHaveBeenCalled();
    expect(chromeMock.sendNativeMessage).not.toHaveBeenCalled();
  });

  it('leaves a Shift-clicked link to Chrome once', async () => {
    chromeMock.sendNativeMessage.mockImplementation(replyWith('ack', { downloadId: 1, status: 'queued' }));
    const bypass = new BypassList();
    bypass.add('https://example.com/files/setup.exe');

    expect(await captureDownload(download(), bypass)).toEqual({ action: 'ignored', reason: 'shift-click' });
    expect((await captureDownload(download(), bypass)).action).toBe('captured');
  });

  it('refreshes stale rules in the background without delaying the capture', async () => {
    chromeMock.storage.rulesFetchedAt = 0;
    chromeMock.sendNativeMessage.mockImplementation(async (_host: string, request: { id: string; type: string }) =>
      request.type === 'get_status'
        ? { v: 1, id: request.id, type: 'status', payload: { active: [], recent: [], captureRules: { enabled: true, minSizeBytes: 1, extensions: ['iso'], excludedDomains: [] } } }
        : { v: 1, id: request.id, type: 'ack', payload: { downloadId: 1, status: 'queued' } });

    await captureDownload(download(), new BypassList());
    await new Promise((resolve) => setImmediate(resolve));

    expect(sentMessages().map((m) => m.type).sort()).toEqual(['add_download', 'get_status']);
    expect((await loadSettings()).rules.extensions).toEqual(['iso']);
  });
});

describe('context menu (FR-21)', () => {
  it('sends the link with the page as referrer, regardless of the capture rules', async () => {
    chromeMock.storage.captureEnabled = false;
    chromeMock.sendNativeMessage.mockImplementation(replyWith('ack', { downloadId: 3, status: 'confirming' }));

    const outcome = await downloadLinkWithOb2('https://example.com/a.html', 'https://example.com/');

    expect(outcome).toEqual({ action: 'captured', downloadId: 3, status: 'confirming' });
    expect(sentMessages()[0].payload).toMatchObject({ url: 'https://example.com/a.html', referrer: 'https://example.com/', source: 'chrome-context-menu' });
    expect(chromeMock.downloads.download).not.toHaveBeenCalled();
  });

  it('lets Chrome download the link when OB2 cannot be reached', async () => {
    chromeMock.sendNativeMessage.mockRejectedValue(new Error('Specified native messaging host not found.'));

    const outcome = await downloadLinkWithOb2('https://example.com/a.zip', undefined);

    expect(outcome).toEqual({ action: 'left-to-chrome', reason: 'not-registered' });
    expect(chromeMock.downloads.download).toHaveBeenCalledWith({ url: 'https://example.com/a.zip' });
  });
});

describe('media button and media menu (FR-24)', () => {
  it('sends the file with the page as referrer and a name taken from the page title', async () => {
    chromeMock.sendNativeMessage.mockImplementation(replyWith('ack', { downloadId: 5, status: 'confirming' }));
    chromeMock.cookies.getAll.mockResolvedValue([{ name: 'sid', value: 'abc' }]);

    const media = { url: 'https://cdn.example.com/videoplayback?id=9', mime: 'video/mp4', size: 50_000_000 };
    const outcome = await downloadMediaWithOb2(media, 'https://example.com/watch/9', 'Launch day / highlights');

    expect(outcome).toEqual({ action: 'captured', downloadId: 5, status: 'confirming' });
    expect(sentMessages()[0].payload).toMatchObject({
      url: media.url,
      referrer: 'https://example.com/watch/9',
      filenameHint: 'Launch day - highlights.mp4',
      mimeType: 'video/mp4',
      fileSizeHint: 50_000_000,
      cookies: 'sid=abc',
      source: 'chrome-media',
      autoStart: false,
    });
  });

  it('never sends anything from an Incognito tab', async () => {
    const outcome = await downloadMediaWithOb2({ url: 'https://e.com/a.mp4' }, 'https://e.com/', 'A', true);

    expect(outcome).toEqual({ action: 'ignored', reason: 'incognito' });
    expect(chromeMock.sendNativeMessage).not.toHaveBeenCalled();
    expect(chromeMock.downloads.download).not.toHaveBeenCalled();
  });

  it('lets Chrome download the file when OB2 cannot be reached', async () => {
    chromeMock.sendNativeMessage.mockRejectedValue(new Error('Specified native messaging host not found.'));

    const outcome = await downloadMediaWithOb2({ url: 'https://e.com/clip.webm' }, 'https://e.com/', 'Clip');

    expect(outcome).toEqual({ action: 'left-to-chrome', reason: 'not-registered' });
    expect(chromeMock.downloads.download).toHaveBeenCalledWith({ url: 'https://e.com/clip.webm' });
  });
});

describe('payload details', () => {
  it('leaves out what Chrome does not know', async () => {
    const payload = await buildPayload({ url: 'https://e.com/a.zip', finalUrl: 'https://e.com/a.zip', filename: '', totalBytes: -1 }, 'chrome-downloads');

    expect(payload).not.toHaveProperty('finalUrl');
    expect(payload).not.toHaveProperty('filenameHint');
    expect(payload).not.toHaveProperty('fileSizeHint');
    expect(payload).not.toHaveProperty('cookies');
    expect(payload).not.toHaveProperty('referrer');
  });

  it('cookie lookup failures mean no cookies, not a failed capture', async () => {
    chromeMock.cookies.getAll.mockRejectedValue(new Error('No host permission'));

    expect(await cookieHeader('https://e.com/')).toBeUndefined();
  });
});

describe('status', () => {
  it('caches the capture rules that arrive with a status reply', async () => {
    const captureRules = { enabled: true, minSizeBytes: 2048, extensions: ['zip'], excludedDomains: ['intranet.local'] };
    chromeMock.sendNativeMessage.mockImplementation(replyWith('status', { active: [], recent: [], captureRules }));

    const status = await fetchStatus();

    expect(status.captureRules).toEqual(captureRules);
    expect((await loadSettings()).rules).toEqual(captureRules);
    expect(sentMessages()[0]).toMatchObject({ type: 'get_status' });
  });

  it('reports the app not running as such', async () => {
    chromeMock.sendNativeMessage.mockImplementation(replyWith('error', { code: 'APP_NOT_RUNNING', message: 'OB2 is not running.' }));

    await expect(fetchStatus()).rejects.toMatchObject({ failure: 'app-not-running', code: 'APP_NOT_RUNNING' });
  });
});

describe('BypassList', () => {
  it('forgets entries after 15 seconds', () => {
    const list = new BypassList();
    list.add('https://e.com/a.zip', 1000);

    expect(list.consume(['https://e.com/a.zip'], 1000 + BypassList.TTL_MS)).toBe(false);
  });

  it('matches the original or the final URL', () => {
    const list = new BypassList();
    list.add('https://cdn.e.com/a.zip', 0);

    expect(list.consume(['https://e.com/a.zip', 'https://cdn.e.com/a.zip'], 10)).toBe(true);
  });
});
