import { decide, DEFAULT_RULES, fileExtension, isExcluded, loadSettings, saveRules, type DownloadFacts, type StoredSettings } from '../src/rules';
import { installChrome } from './chromeMock';

const settings = (overrides: Partial<StoredSettings> = {}): StoredSettings => ({
  captureEnabled: true,
  rules: DEFAULT_RULES,
  rulesFetchedAt: 0,
  ...overrides,
});

const item = (overrides: Partial<DownloadFacts> = {}): DownloadFacts => ({
  url: 'https://example.com/files/big.zip',
  totalBytes: 50 * 1024 * 1024,
  state: 'in_progress',
  ...overrides,
});

describe('capture rules (§8.4)', () => {
  it('captures a large file of a listed type', () => {
    expect(decide(item(), settings(), false)).toEqual({ capture: true });
  });

  it.each([
    ['switched off in the popup', settings({ captureEnabled: false }), item(), 'switched-off'],
    ['disabled in the app', settings({ rules: { ...DEFAULT_RULES, enabled: false } }), item(), 'disabled-in-app'],
    ['started by an extension', settings(), item({ byExtensionId: 'abc' }), 'started-by-extension'],
    ['incognito', settings(), item({ incognito: true }), 'incognito'],
    ['already complete', settings(), item({ state: 'complete' }), 'not-in-progress'],
    ['blob url', settings(), item({ url: 'blob:https://example.com/123' }), 'not-http'],
    ['data url', settings(), item({ url: 'data:application/zip;base64,AAAA' }), 'not-http'],
    ['smaller than the minimum', settings(), item({ totalBytes: 500_000 }), 'too-small'],
    ['type not listed', settings(), item({ url: 'https://example.com/page.html' }), 'file-type'],
    ['no type at all', settings(), item({ url: 'https://example.com/download' }), 'file-type'],
  ])('skips a download that is %s', (_name, s, i, reason) => {
    expect(decide(i, s, false)).toEqual({ capture: false, reason });
  });

  it('leaves a Shift-clicked link to Chrome', () => {
    expect(decide(item(), settings(), true)).toEqual({ capture: false, reason: 'shift-click' });
  });

  it('captures a listed type of unknown size', () => {
    expect(decide(item({ totalBytes: -1, fileSize: 0 }), settings(), false)).toEqual({ capture: true });
  });

  it('checks the final URL scheme, not just the first one', () => {
    expect(decide(item({ finalUrl: 'ftp://mirror.example.com/big.zip' }), settings(), false)).toEqual({ capture: false, reason: 'not-http' });
  });

  it('honours the size limit the app sent', () => {
    const rules = { ...DEFAULT_RULES, minSizeBytes: 100 * 1024 * 1024 };
    expect(decide(item(), settings({ rules }), false)).toEqual({ capture: false, reason: 'too-small' });
  });

  it('captures every type when the app sends an empty type list', () => {
    const rules = { ...DEFAULT_RULES, extensions: [] };
    expect(decide(item({ url: 'https://example.com/download' }), settings({ rules }), false)).toEqual({ capture: true });
  });

  it('excludes by download host and by the page it came from', () => {
    const rules = { ...DEFAULT_RULES, excludedDomains: ['example.com'] };
    expect(decide(item({ url: 'https://dl.example.com/a.zip' }), settings({ rules }), false)).toEqual({ capture: false, reason: 'excluded-domain' });
    expect(decide(item({ url: 'https://cdn.other.net/a.zip', referrer: 'https://example.com/page' }), settings({ rules }), false)).toEqual({ capture: false, reason: 'excluded-domain' });
  });
});

describe('isExcluded', () => {
  it.each([
    ['example.com', ['example.com'], true],
    ['www.example.com', ['example.com'], true],
    ['EXAMPLE.com', ['Example.COM'], true],
    ['example.com.', ['example.com'], true],
    ['www.example.com', ['*.example.com'], true],
    ['badexample.com', ['example.com'], false],
    ['example.com.evil.net', ['example.com'], false],
    ['example.com', ['', '  '], false],
  ])('%s against %j is %s', (host, domains, expected) => {
    expect(isExcluded(host, domains)).toBe(expected);
  });
});

describe('fileExtension', () => {
  it('prefers the file name Chrome chose', () => {
    expect(fileExtension({ url: 'https://e.com/get?id=1', filename: 'C:\\Users\\me\\Downloads\\Report.PDF' })).toBe('pdf');
  });

  it('uses the final URL, then the original URL, decoding the path', () => {
    expect(fileExtension({ url: 'https://e.com/r/1', finalUrl: 'https://cdn.e.com/My%20File.tar.gz?sig=x' })).toBe('gz');
    expect(fileExtension({ url: 'https://e.com/a/movie.mkv#t=3' })).toBe('mkv');
  });

  it('falls back to the MIME type for download?id= links', () => {
    expect(fileExtension({ url: 'https://e.com/download?id=5', mime: 'application/x-zip-compressed' })).toBe('zip');
    expect(fileExtension({ url: 'https://e.com/download?id=5', mime: 'video/mp4; codecs=avc1' })).toBe('mp4');
  });

  it('ignores dot files and trailing dots', () => {
    expect(fileExtension({ url: 'https://e.com/.hidden' })).toBeUndefined();
    expect(fileExtension({ url: 'https://e.com/name.' })).toBeUndefined();
  });
});

describe('stored settings', () => {
  beforeEach(() => installChrome());

  it('defaults to capturing with the app defaults', async () => {
    expect(await loadSettings()).toEqual({ captureEnabled: true, rules: DEFAULT_RULES, rulesFetchedAt: 0 });
  });

  it('keeps rules from the app with their time', async () => {
    const rules = { enabled: true, minSizeBytes: 5, extensions: ['iso'], excludedDomains: ['x.org'] };
    await saveRules(rules, 1234);
    expect(await loadSettings()).toMatchObject({ rules, rulesFetchedAt: 1234 });
  });

  it('ignores malformed rules', async () => {
    await saveRules({ enabled: 'yes' } as never, 1);
    expect((await loadSettings()).rules).toEqual(DEFAULT_RULES);
  });
});
