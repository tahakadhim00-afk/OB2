import { formatBytes } from '../src/format';
import { isDownloadableMediaUrl, isMediaCandidate, MEDIA_MIN_BYTES, mediaFileName, mediaFromResponse, mediaLabel } from '../src/media';

const response = (url: string, headers: Record<string, string>, statusCode = 200) => ({
  url,
  statusCode,
  responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })),
});

describe('media detection (FR-24)', () => {
  it('finds a whole video file and its size', () => {
    expect(mediaFromResponse(response('https://cdn.e.com/v/clip.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': '52428800' }))).toEqual({
      url: 'https://cdn.e.com/v/clip.mp4',
      mime: 'video/mp4',
      size: 52_428_800,
    });
  });

  it('reads the whole size of a ranged reply from Content-Range, not the chunk length', () => {
    const media = mediaFromResponse(response('https://e.com/a.webm', { 'content-type': 'video/webm', 'content-length': '65536', 'content-range': 'bytes 0-65535/90000000' }, 206));

    expect(media).toEqual({ url: 'https://e.com/a.webm', mime: 'video/webm', size: 90_000_000 });
  });

  it('accepts a generic type when the address names a media file, and an unknown size', () => {
    expect(mediaFromResponse(response('https://e.com/files/talk.mp3', { 'Content-Type': 'application/octet-stream' }))).toEqual({ url: 'https://e.com/files/talk.mp3' });
  });

  it.each([
    ['an HLS playlist', 'https://e.com/live/index.m3u8', { 'Content-Type': 'application/vnd.apple.mpegurl' }],
    ['an HLS segment', 'https://e.com/live/seg-12.ts', { 'Content-Type': 'video/mp2t', 'Content-Length': '9000000' }],
    ['a DASH manifest', 'https://e.com/v/manifest.mpd', { 'Content-Type': 'application/dash+xml' }],
    ['a DASH segment', 'https://e.com/v/chunk-3.m4s', { 'Content-Type': 'video/mp4', 'Content-Length': '9000000' }],
    ['a byte-range chunk URL', 'https://r1.googlevideo.com/videoplayback?id=1&range=0-1048575', { 'Content-Type': 'video/webm' }],
    ['a small preview', 'https://e.com/preview.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': String(MEDIA_MIN_BYTES - 1) }],
    ['a web page', 'https://e.com/watch', { 'Content-Type': 'text/html' }],
    ['a binary file without a media name', 'https://e.com/blob', { 'Content-Type': 'application/octet-stream' }],
    ['a data: address', 'data:video/mp4;base64,AAAA', { 'Content-Type': 'video/mp4' }],
  ])('leaves out %s', (_name, url, headers) => {
    expect(mediaFromResponse(response(url, headers))).toBeUndefined();
  });

  it('ignores failed replies', () => {
    expect(mediaFromResponse(response('https://e.com/a.mp4', { 'Content-Type': 'video/mp4' }, 404))).toBeUndefined();
  });

  it('only lets through addresses OB2 can download as one file', () => {
    expect(isDownloadableMediaUrl('https://e.com/a.mp4')).toBe(true);
    expect(isDownloadableMediaUrl('blob:https://e.com/1234')).toBe(false);
    expect(isDownloadableMediaUrl('https://e.com/index.m3u8')).toBe(false);
    expect(isMediaCandidate({ url: 'https://e.com/a.mp4', size: 5 })).toBe(true);
    expect(isMediaCandidate({ url: 'javascript:alert(1)' })).toBe(false);
    expect(isMediaCandidate({ url: 'https://e.com/a.mp4', size: '5' })).toBe(false);
  });
});

describe('media names', () => {
  it('names a file after the page when the address has no file name', () => {
    expect(mediaFileName({ url: 'https://e.com/videoplayback?id=1', mime: 'audio/mp4' }, '  My   song  ')).toBe('My song.m4a');
  });

  it('keeps the file name in the address, and gives none for an unknown type or title', () => {
    expect(mediaFileName({ url: 'https://e.com/talk.mp4', mime: 'video/mp4' }, 'Talk')).toBeUndefined();
    expect(mediaFileName({ url: 'https://e.com/stream', mime: 'video/x-unknown' }, 'Talk')).toBeUndefined();
    expect(mediaFileName({ url: 'https://e.com/stream', mime: 'video/mp4' }, '')).toBeUndefined();
  });

  it('labels menu entries with the file name and size', () => {
    expect(mediaLabel({ url: 'https://e.com/v/My%20clip.mp4', size: 25 * 1024 * 1024 }, formatBytes)).toBe('My clip.mp4 · 25 MB');
    expect(mediaLabel({ url: 'https://cdn.e.com/', mime: 'audio/mpeg' }, formatBytes)).toBe('cdn.e.com · audio');
  });
});
