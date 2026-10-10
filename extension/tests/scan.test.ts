/**
 * @jest-environment jsdom
 */
import { isPageScan, kindOfExtension, scanPage } from '../src/scan';

function page(html: string, base = 'https://site.example/watch/42'): Document {
  const doc = document.implementation.createHTMLDocument('Launch video');
  doc.head.insertAdjacentHTML('beforeend', `<base href="${base}">`);
  doc.body.innerHTML = html;
  return doc;
}

const urls = (doc: Document) => scanPage(doc).links.map((l) => l.url);

describe('page scan (D-70)', () => {
  it('finds file links, resolving relative addresses against the page', () => {
    const scan = scanPage(page('<a href="/files/setup.exe">Get it</a><a href="report.pdf">PDF</a><a href="/about.html">About</a>'));

    expect(scan.links).toEqual([
      { url: 'https://site.example/files/setup.exe', name: 'setup.exe', kind: 'file' },
      { url: 'https://site.example/watch/report.pdf', name: 'report.pdf', kind: 'file' },
    ]);
    expect(scan.title).toBe('Launch video');
  });

  it('takes a link marked download as a file, with the page’s name for it', () => {
    const [link] = scanPage(page('<a href="/get?id=7" download="Manual v2.pdf">Manual</a>')).links;

    expect(link).toEqual({ url: 'https://site.example/get?id=7', name: 'Manual v2.pdf', kind: 'file', downloadName: 'Manual v2.pdf' });
  });

  it('reads video and audio elements and their <source> children', () => {
    const doc = page('<video src="https://cdn.example/stream?id=1"></video><audio><source src="/a/song.mp3"></audio><video src="blob:https://site.example/1"></video>');

    expect(scanPage(doc).links.map((l) => [l.url, l.kind])).toEqual([
      ['https://cdn.example/stream?id=1', 'video'],
      ['https://site.example/a/song.mp3', 'audio'],
    ]);
  });

  it('finds addresses written in scripts, including escaped JSON', () => {
    const doc = page(`<div id="player"></div>
      <script>var config = {"sources":[{"file":"https:\\/\\/cdn.example\\/v\\/clip_720.mp4?token=a\\u0026exp=9","label":"720p"}],
      "hls":"https://cdn.example/v/master.m3u8"};</script>
      <script>player.load('//media.example/audio/talk.m4a');</script>`);

    expect(scanPage(doc).links.map((l) => [l.url, l.kind])).toEqual([
      ['https://cdn.example/v/clip_720.mp4?token=a&exp=9', 'video'],
      ['https://cdn.example/v/master.m3u8', 'stream'],
      ['https://media.example/audio/talk.m4a', 'audio'],
    ]);
  });

  it('reads media meta tags', () => {
    const doc = page('');
    doc.head.insertAdjacentHTML('beforeend', '<meta property="og:video:secure_url" content="https://cdn.example/og?id=3">');

    expect(scanPage(doc).links).toEqual([{ url: 'https://cdn.example/og?id=3', name: 'og', kind: 'video' }]);
  });

  it('lists media the frame loaded first, and each address once', () => {
    const doc = page('<a href="https://cdn.example/x.mp4">x</a><p>https://cdn.example/x.mp4</p>');

    const links = scanPage(doc, [{ url: 'https://cdn.example/played', mime: 'audio/mp4' }]).links;

    expect(links.map((l) => [l.url, l.kind])).toEqual([
      ['https://cdn.example/played', 'audio'],
      ['https://cdn.example/x.mp4', 'video'],
    ]);
  });

  it('skips segments, pages, data: addresses and look-alike extensions', () => {
    const doc = page(`<script>
      a = "https://cdn.example/seg-001.ts"; b = "https://cdn.example/chunk.m4s"; c = "https://site.example/page.php";
      d = "https://site.example/app.mp4.html"; e = "javascript:go('x.zip')";
    </script><img src="data:image/png;base64,${'Ly8v'.repeat(5000)}.zip">`);

    expect(urls(doc)).toEqual([]);
  });

  it('classifies extensions', () => {
    expect([kindOfExtension('mkv'), kindOfExtension('flac'), kindOfExtension('mpd'), kindOfExtension('docx'), kindOfExtension('html')]).toEqual([
      'video', 'audio', 'stream', 'file', undefined,
    ]);
  });

  it('checks scans that come back from frames', () => {
    expect(isPageScan(scanPage(page('<a href="a.zip">a</a>')))).toBe(true);
    expect(isPageScan({ pageUrl: 'x', title: '', links: [{ url: 'javascript:1', name: 'a', kind: 'file' }] })).toBe(false);
    expect(isPageScan(null)).toBe(false);
  });
});
