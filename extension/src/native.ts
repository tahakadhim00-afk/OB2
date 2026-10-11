// One request, one reply: chrome.runtime.sendNativeMessage starts host.exe for each call (§8.2).

import { ErrorCode, HOST_NAME, MessageType, type ErrorPayload, type ProtocolMessage } from './protocol';

/** Why a request did not get a useful reply. */
export type HostFailure =
  | 'not-registered' // host manifest or registry key missing: OB2 Options > Browser > Register
  | 'forbidden' // allowed_origins does not list this extension id
  | 'host-exited' // host.exe crashed or was blocked (antivirus)
  | 'timeout'
  | 'app-not-running'
  | 'error-reply'
  | 'unknown';

export class HostError extends Error {
  constructor(
    readonly failure: HostFailure,
    message: string,
    readonly code?: string,
  ) {
    super(message);
    this.name = 'HostError';
  }
}

/** §8.3: the host waits up to 15 s for a freshly started app, so captures allow a little more. */
export const CAPTURE_TIMEOUT_MS = 20_000;
export const STATUS_TIMEOUT_MS = 5_000;
/** D-72: the host may start OB2 (15 s), then gives yt-dlp up to 90 s to list a video's qualities. */
export const MEDIA_FORMATS_TIMEOUT_MS = 110_000;

export async function sendToHost<TReply = unknown>(message: ProtocolMessage, timeoutMs: number): Promise<ProtocolMessage<TReply>> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new HostError('timeout', `OB2 did not answer within ${timeoutMs / 1000} s.`)), timeoutMs);
  });

  let reply: ProtocolMessage<TReply> | undefined;
  try {
    reply = (await Promise.race([chrome.runtime.sendNativeMessage(HOST_NAME, message), timeout])) as ProtocolMessage<TReply> | undefined;
  } catch (error) {
    throw error instanceof HostError ? error : classify(error);
  } finally {
    clearTimeout(timer);
  }

  if (!reply || typeof reply.type !== 'string') {
    throw new HostError('unknown', 'OB2 sent an empty reply.');
  }

  if (reply.type === MessageType.Error) {
    const error = reply.payload as ErrorPayload | undefined;
    const failure = error?.code === ErrorCode.AppNotRunning ? 'app-not-running' : 'error-reply';
    throw new HostError(failure, error?.message ?? 'OB2 reported an error.', error?.code);
  }

  return reply;
}

function classify(error: unknown): HostError {
  const text = error instanceof Error ? error.message : String(error);
  if (/not found/i.test(text)) {
    return new HostError('not-registered', text);
  }
  if (/forbidden/i.test(text)) {
    return new HostError('forbidden', text);
  }
  if (/exited|disconnected/i.test(text)) {
    return new HostError('host-exited', text);
  }
  return new HostError('unknown', text);
}
