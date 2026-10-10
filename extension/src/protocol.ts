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
} as const;

export const ErrorCode = {
  AppNotRunning: 'APP_NOT_RUNNING',
  InvalidUrl: 'INVALID_URL',
  UnsupportedScheme: 'UNSUPPORTED_SCHEME',
  PipeTimeout: 'PIPE_TIMEOUT',
  ProtocolVersionMismatch: 'PROTOCOL_VERSION_MISMATCH',
  InternalError: 'INTERNAL_ERROR',
  InvalidMessage: 'INVALID_MESSAGE',
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
  /** `chrome-media`: the button over a video or the video/audio context menu (FR-24). */
  source: 'chrome-downloads' | 'chrome-context-menu' | 'chrome-media';
  autoStart: boolean;
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
