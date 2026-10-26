import { formatBytes } from '../src/format';
import {
  isDownloadableMediaUrl, isMediaCandidate, isMediaChoice, isRequestHeaders, isStreamAddress, isStreamInfo, MEDIA_MIN_BYTES, mediaFileName, mediaFromResponse, mediaLabel,
  requestHeadersOf, splitRequestHeaders, streamFromResponse,
} from '../src/media';

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

describe('streams a player loads, for yt-dlp (D-79)', () => {
  it.each([
    ['an HLS playlist by its type', 'https://cdn.e.com/v/master.m3u8?token=abc', { 'Content-Type': 'application/vnd.apple.mpegurl' }],
    ['an HLS playlist with an unusual name', 'https://cdn.e.com/play?id=7', { 'Content-Type': 'application/x-mpegURL; charset=utf-8' }],
    ['a DASH manifest by its type', 'https://cdn.e.com/v/stream', { 'Content-Type': 'application/dash+xml' }],
    ['a playlist served as text', 'https://cdn.e.com/hls/index.m3u8', { 'Content-Type': 'text/plain' }],
    ['a manifest served with no useful type', 'https://cdn.e.com/dash/manifest.mpd', { 'Content-Type': 'application/octet-stream' }],
  ])('keeps %s', (_name, url, headers) => {
    expect(streamFromResponse(response(url, headers))).toBe(url);
    expect(mediaFromResponse(response(url, headers))).toBeUndefined(); // never offered as a file
  });

  it.each([
    ['a segment', 'https://cdn.e.com/hls/seg-12.ts', { 'Content-Type': 'video/mp2t' }],
    ['a DASH segment', 'https://cdn.e.com/dash/chunk-3.m4s', { 'Content-Type': 'video/iso.segment' }],
    ['an error page under the playlist name', 'https://cdn.e.com/hls/index.m3u8', { 'Content-Type': 'text/html' }],
    ['a video file', 'https://cdn.e.com/a.mp4', { 'Content-Type': 'video/mp4' }],
    ['an image', 'https://cdn.e.com/thumb.m3u8', { 'Content-Type': 'image/png' }],
  ])('leaves out %s', (_name, url, headers) => {
    expect(streamFromResponse(response(url, headers))).toBeUndefined();
  });

  it('ignores failed replies and addresses OB2 would refuse', () => {
    expect(streamFromResponse(response('https://e.com/index.m3u8', { 'Content-Type': 'application/vnd.apple.mpegurl' }, 403))).toBeUndefined();
    expect(streamFromResponse(response(`https://e.com/index.m3u8?t=${'a'.repeat(2048)}`, { 'Content-Type': 'application/vnd.apple.mpegurl' }))).toBeUndefined();
    expect(isStreamAddress('https://e.com/index.m3u8')).toBe(true);
    expect(isStreamAddress('blob:https://e.com/1')).toBe(false);
    expect(isStreamAddress(7)).toBe(false);
  });

  it('lets a stream travel only as a video page, which goes to yt-dlp and never to Chrome', () => {
    expect(isMediaCandidate({ url: 'https://e.com/index.m3u8', page: true, format: 'hls-720', container: 'mp4' })).toBe(true);
    expect(isMediaCandidate({ url: 'https://e.com/index.m3u8' })).toBe(false);
  });
});

describe('the request a player made for its stream (D-80)', () => {
  // As chrome.webRequest.onSendHeaders reports movie-box.co's request for its DASH manifest.
  const sent = [
    { name: 'Origin', value: 'https://movie-box.co' },
    { name: 'Referer', value: 'https://movie-box.co/' },
    { name: 'User-Agent', value: 'Mozilla/5.0 Chrome/141' },
    { name: 'X-MB-Token', value: 'Edge-Cache-Cookie=urlprefix=aHR0cHM6Ly9z:Expires=1793012617' },
    { name: 'Accept', value: '*/*' },
    { name: 'Cookie', value: 'sid=secret' },
    { name: 'Authorization', value: 'Bearer secret' },
    { name: 'Range', value: 'bytes=0-' },
    { name: 'Accept-Encoding', value: 'gzip, br' },
    { name: 'X-Broken', value: 'a\r\nHost: elsewhere' },
  ];

  it('keeps what the server may check, and leaves credentials and per-request headers to OB2', () => {
    expect(requestHeadersOf(sent)).toEqual({
      Origin: 'https://movie-box.co',
      Referer: 'https://movie-box.co/',
      'User-Agent': 'Mozilla/5.0 Chrome/141',
      'X-MB-Token': 'Edge-Cache-Cookie=urlprefix=aHR0cHM6Ly9z:Expires=1793012617',
      Accept: '*/*',
    });
    expect(requestHeadersOf(undefined)).toEqual({});
  });

  it('sends the Referer and User-Agent in fields of their own', () => {
    expect(splitRequestHeaders(requestHeadersOf(sent))).toEqual({
      referrer: 'https://movie-box.co/',
      userAgent: 'Mozilla/5.0 Chrome/141',
      headers: { Origin: 'https://movie-box.co', 'X-MB-Token': 'Edge-Cache-Cookie=urlprefix=aHR0cHM6Ly9z:Expires=1793012617', Accept: '*/*' },
    });
  });

  it('checks headers that come from another context', () => {
    expect(isRequestHeaders({ Origin: 'https://e.com' })).toBe(true);
    expect(isRequestHeaders({ 'Bad Name': 'x' })).toBe(false);
    expect(isRequestHeaders({ X: 'a\nb' })).toBe(false);
    expect(isRequestHeaders({ X: 1 })).toBe(false);
    expect(isRequestHeaders(['x'])).toBe(false);
    expect(isStreamInfo({ url: 'https://e.com/a.mpd', headers: {} })).toBe(true);
    expect(isStreamInfo({ url: 'https://e.com/a.mpd' })).toBe(false);
    expect(isMediaCandidate({ url: 'https://e.com/a.mpd', page: true, format: 'stream:v2+a0', container: 'mp4', headers: { Origin: 'https://e.com' } })).toBe(true);
    expect(isMediaCandidate({ url: 'https://e.com/a.mp4', headers: { Origin: 'https://e.com' } })).toBe(false); // only a stream carries them
  });

  it('accepts the stream qualities OB2 offers, and nothing that only looks like one', () => {
    expect(isMediaChoice('stream:v1+a0', 'mp4')).toBe(true);
    expect(isMediaChoice('stream:a0', 'm4a')).toBe(true);
    expect(isMediaChoice('stream:v2', 'mp4')).toBe(true);
    expect(isMediaChoice('stream:a0+v1', 'mp4')).toBe(false);
    expect(isMediaChoice('stream:v1;rm', 'mp4')).toBe(false);
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
