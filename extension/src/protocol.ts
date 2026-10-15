// Protocol v1 (PRD §8.3). Mirrors src/OB2.Shared/Protocol/Messages.cs; see docs/protocol.md.

export const PROTOCOL_VERSION = 1;
export const HOST_NAME = 'com.ob2.host';

export const MessageType = {
  AddDownload: 'add_download',
  Ack: 'ack',
  Ping: 'ping',
  Pong: 'pong',
  GetStatus: 'get_status',
  Status: 'status',
  PauseAll: 'pause_all',
  ResumeAll: 'resume_all',
  Error: 'error',
  /** D-72: the qualities a video page offers, for the button's menu. */
  GetMediaFormats: 'get_media_formats',
  MediaFormats: 'media_formats',
} as const;

export const ErrorCode = {
  AppNotRunning: 'APP_NOT_RUNNING',
  InvalidUrl: 'INVALID_URL',
  UnsupportedScheme: 'UNSUPPORTED_SCHEME',
  PipeTimeout: 'PIPE_TIMEOUT',
  ProtocolVersionMismatch: 'PROTOCOL_VERSION_MISMATCH',
  InternalError: 'INTERNAL_ERROR',
  InvalidMessage: 'INVALID_MESSAGE',
  MediaToolsMissing: 'MEDIA_TOOLS_MISSING',
  MediaFailed: 'MEDIA_FAILED',
} as const;

export interface ProtocolMessage<T = unknown> {
  v: number;
  id: string;
  type: string;
  payload?: T;
}

export interface AddDownloadPayload {
  url: string;
  finalUrl?: string;
  referrer?: string;
  filenameHint?: string;
  mimeType?: string;
  fileSizeHint?: number;
  userAgent?: string;
  /** Cookie header value. Sent to the host only; never logged or stored by the extension. */
  cookies?: string;
  headers?: Record<string, string>;
  /**
   * `chrome-media`: the button over a video or the video/audio context menu (FR-24).
   * `chrome-page`: a link picked from the popup's "Find downloads" list (D-70).
   */
  source: 'chrome-downloads' | 'chrome-context-menu' | 'chrome-media' | 'chrome-page';
  autoStart: boolean;
  /** A video page (YouTube and others) for OB2's quality chooser, not a file (D-71). */
  mediaPage?: boolean;
  /**
   * D-72: with `mediaPage`, the quality picked on the button: a `format` and `container` from `media_formats`.
   * D-74: or a subtitle track's `format` (`subs:en`) and one of its `containers` (`srt`).
   */
  mediaFormat?: string;
  mediaContainer?: string;
}

export interface MediaFormatsRequest {
  url: string;
}

/** One quality on the button's menu (D-72). `format` and `container` go back in `add_download`. */
export interface MediaOption {
  format: string;
  container: string;
  audioOnly: boolean;
  /** "1080p60" for a picture, "129 kbit/s" for sound alone. */
  quality?: string;
  codec?: string;
  /** The sound track's language, when the video has several. */
  track?: string;
  size?: number;
}

/** D-74: one subtitle track; `format` and one of `containers` go back in `add_download`. */
export interface MediaSubtitle {
  format: string;
  language: string;
  name: string;
  /** Made by the site's speech recognition (YouTube's auto-generated captions). */
  automatic: boolean;
  containers: string[];
}

export interface MediaFormatsPayload {
  title: string;
  durationSeconds?: number;
  options: MediaOption[];
  /** D-74: empty when the video has none; absent from an OB2 that predates subtitles. */
  subtitles?: MediaSubtitle[];
}

export interface AckPayload {
  downloadId?: number | null;
  status: string;
}

export interface ErrorPayload {
  code: string;
  message: string;
}

export interface PongPayload {
  appRunning: boolean;
  hostVersion: string;
}

export interface DownloadSummary {
  id: number;
  fileName: string;
  status: string;
  progress?: number | null;
  bytesPerSecond: number;
  totalBytes?: number | null;
}

export interface CaptureRules {
  enabled: boolean;
  minSizeBytes: number;
  extensions: string[];
  excludedDomains: string[];
}

export interface StatusPayload {
  active: DownloadSummary[];
  recent: DownloadSummary[];
  captureRules: CaptureRules;
}

export function createMessage<T>(type: string, payload?: T): ProtocolMessage<T> {
  const message: ProtocolMessage<T> = { v: PROTOCOL_VERSION, id: crypto.randomUUID(), type };
  if (payload !== undefined) {
    message.payload = payload;
  }
  return message;
}
