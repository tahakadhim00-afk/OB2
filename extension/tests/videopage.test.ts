/**
 * @jest-environment jsdom
 * @jest-environment-options {"url": "https://news.example/world/launch-video"}
 */
import type { FormatsOutcome } from '../src/capture';
import type { StreamInfo } from '../src/media';
import { MediaOverlay } from '../src/overlay';

const PAGE = 'https://news.example/world/launch-video';

function addVideo(src: string): HTMLVideoElement {
  const video = document.createElement('video');
  video.setAttribute('src', src);
  video.getBoundingClientRect = () => ({ left: 0, top: 0, right: 640, bottom: 360, width: 640, height: 360, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
  document.body.append(video);
  return video;
}

describe('a streaming player on another site (D-71)', () => {
  beforeEach(() => (document.body.innerHTML = ''));

  it('offers the files it loaded first, then the whole page for the quality chooser', () => {
    const overlay = new MediaOverlay(document, jest.fn(), 'open');
    const video = addVideo('blob:https://news.example/1');
    overlay.addDetected({ url: 'https://cdn.news.example/clip.mp4', size: 9_000_000 });

    expect(overlay.offersFor(video)).toEqual([
      { url: 'https://cdn.news.example/clip.mp4', size: 9_000_000 },
      { url: 'https://news.example/world/launch-video', page: true },
    ]);
  });

  it('offers only the page when nothing else is known, so the button still appears', () => {
    const overlay = new MediaOverlay(document, jest.fn(), 'open');
    const video = addVideo('blob:https://news.example/2');

    expect(overlay.show(video)).toBe(true);
    expect(overlay.offersFor(video)).toEqual([{ url: 'https://news.example/world/launch-video', page: true }]);
  });

  it('a plain file needs no quality chooser', () => {
    const overlay = new MediaOverlay(document, jest.fn(), 'open');

    expect(overlay.offersFor(addVideo('https://cdn.news.example/a.mp4'))).toEqual([{ url: 'https://cdn.news.example/a.mp4' }]);
  });
});

describe('a site yt-dlp does not know (D-79)', () => {
  const MASTER = 'https://cdn.news.example/hls/master.m3u8?token=1';
  const VARIANT = 'https://cdn.news.example/hls/720/index.m3u8?token=1';
  const LISTED: FormatsOutcome = {
    action: 'listed',
    title: 'Launch video - News',
    url: MASTER,
    options: [{ format: '720', container: 'mp4', audioOnly: false, quality: '720p', codec: 'H.264' }],
  };

  /** D-80: what Chrome sent with the player's request for the playlist. */
  const SENT = { Origin: 'https://news.example', Referer: 'https://news.example/', 'X-Player-Token': 'abc' };

  let send: jest.Mock;
  let listFormats: jest.Mock<Promise<FormatsOutcome>, [string, readonly StreamInfo[]]>;
  let overlay: MediaOverlay;
  let video: HTMLVideoElement;

  const shadow = () => document.querySelector('ob2-media-button')!.shadowRoot!;
  const main = () => shadow().querySelector('.main') as HTMLButtonElement;
  const items = () => Array.from(shadow().querySelectorAll<HTMLButtonElement>('.item'));
  const notes = () => Array.from(shadow().querySelectorAll('.note'), (n) => n.textContent);
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

  beforeEach(() => {
    document.body.innerHTML = '';
    document.querySelectorAll('ob2-media-button').forEach((b) => b.remove()); // the button sits on <html>
    history.replaceState(null, '', PAGE);
    send = jest.fn().mockResolvedValue('sent');
    listFormats = jest.fn().mockResolvedValue(LISTED);
    overlay = new MediaOverlay(document, send, 'open', listFormats);
    video = addVideo('blob:https://news.example/3');
  });

  afterEach(() => {
    overlay.hide();
    document.querySelector('ob2-media-button')?.remove();
  });

  it('names the streams the player loaded, oldest first, once each, with what the browser sent', async () => {
    overlay.addStream(MASTER, SENT);
    overlay.addStream(VARIANT, { ...SENT, 'X-Player-Token': 'old' });
    overlay.addStream(VARIANT, SENT); // a playlist loaded again: the player's latest token counts
    overlay.show(video);

    main().click();
    await settle();

    expect(listFormats).toHaveBeenCalledWith(PAGE, [{ url: MASTER, headers: SENT }, { url: VARIANT, headers: SENT }]);
  });

  it('downloads the stream OB2 read, with the qualities it found there and the headers it needs', async () => {
    overlay.addStream(MASTER, SENT);
    overlay.show(video);

    main().click();
    await settle();
    expect(items().map((i) => i.textContent)).toEqual(['720p · MP4 (H.264)', 'More choices in OB2… (audio track, folder)']);

    items()[0].click();
    expect(send).toHaveBeenCalledWith({ url: MASTER, page: true, format: '720', container: 'mp4', name: 'Launch video - News', headers: SENT });
    items().at(-1)!.click();
    expect(send).toHaveBeenLastCalledWith({ url: MASTER, page: true, headers: SENT });
  });

  it('leaves out a stream that played on another page of the site, and asks again once one plays here', async () => {
    overlay.addStream('https://cdn.news.example/hls/other.m3u8');
    history.pushState(null, '', '/world/launch-video#comments');
    history.pushState(null, '', '/world/next-video');
    overlay.show(video);

    main().click();
    await settle();
    expect(listFormats).toHaveBeenLastCalledWith('https://news.example/world/next-video', []);

    main().click(); // closes
    overlay.addStream(MASTER);
    main().click();
    await settle();
    expect(listFormats).toHaveBeenLastCalledWith('https://news.example/world/next-video', [{ url: MASTER, headers: {} }]);
    expect(listFormats).toHaveBeenCalledTimes(2);
  });

  it('says a copy-protected video cannot be downloaded, without asking OB2 when there is nothing to try', async () => {
    Object.defineProperty(video, 'mediaKeys', { value: {}, configurable: true });
    overlay.show(video);

    main().click();
    await settle();

    expect(listFormats).not.toHaveBeenCalled();
    expect(notes()).toEqual(['This video is copy-protected (DRM), so OB2 cannot download it.']);
    expect(items()).toHaveLength(0);
  });

  it('tries the streams of a copy-protected player, and explains why they failed', async () => {
    Object.defineProperty(video, 'mediaKeys', { value: {}, configurable: true });
    overlay.addStream(MASTER);
    listFormats.mockResolvedValueOnce({ action: 'failed', reason: 'media-failed', message: 'yt-dlp does not support this site, and could not read the video’s stream either: This video is DRM protected' });
    overlay.show(video);

    main().click();
    await settle();

    expect(listFormats).toHaveBeenCalledWith(PAGE, [{ url: MASTER, headers: {} }]);
    expect(notes()).toEqual(['This video is copy-protected (DRM), so OB2 cannot download it.']);
    expect(items()).toHaveLength(0);
  });
});
