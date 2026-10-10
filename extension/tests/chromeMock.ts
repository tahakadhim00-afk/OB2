// A small stand-in for the chrome.* APIs the extension uses. Each test installs a fresh one.

export interface ChromeMock {
  storage: Record<string, unknown>;
  sendNativeMessage: jest.Mock;
  downloads: { pause: jest.Mock; resume: jest.Mock; cancel: jest.Mock; erase: jest.Mock; download: jest.Mock };
  cookies: { getAll: jest.Mock };
}

export function installChrome(): ChromeMock {
  const storage: Record<string, unknown> = {};
  const mock: ChromeMock = {
    storage,
    sendNativeMessage: jest.fn(),
    downloads: {
      pause: jest.fn().mockResolvedValue(undefined),
      resume: jest.fn().mockResolvedValue(undefined),
      cancel: jest.fn().mockResolvedValue(undefined),
      erase: jest.fn().mockResolvedValue([]),
      download: jest.fn().mockResolvedValue(99),
    },
    cookies: { getAll: jest.fn().mockResolvedValue([]) },
  };

  (globalThis as unknown as { chrome: unknown }).chrome = {
    runtime: { sendNativeMessage: mock.sendNativeMessage, id: 'mgmmgplfngnhcencbhpfagnjcnenflpm' },
    downloads: mock.downloads,
    cookies: mock.cookies,
    storage: {
      local: {
        get: jest.fn(async (keys: string[]) => Object.fromEntries(keys.filter((k) => k in storage).map((k) => [k, storage[k]]))),
        set: jest.fn(async (values: Record<string, unknown>) => {
          Object.assign(storage, values);
        }),
      },
    },
  };
  return mock;
}

/** The host's reply to whatever was sent, keeping the request id like the app does. */
export function replyWith(type: string, payload?: unknown) {
  return async (_host: string, request: { id: string }) => ({ v: 1, id: request.id, type, payload });
}

export function download(overrides: Partial<chrome.downloads.DownloadItem> = {}): chrome.downloads.DownloadItem {
  return {
    id: 7,
    url: 'https://example.com/files/setup.exe',
    finalUrl: 'https://cdn.example.com/abc/setup.exe',
    referrer: 'https://example.com/downloads',
    filename: '',
    mime: 'application/octet-stream',
    totalBytes: 524_288_000,
    fileSize: 524_288_000,
    bytesReceived: 0,
    state: 'in_progress',
    incognito: false,
    danger: 'safe',
    paused: false,
    canResume: true,
    exists: true,
    startTime: new Date(0).toISOString(),
    ...overrides,
  } as chrome.downloads.DownloadItem;
}
