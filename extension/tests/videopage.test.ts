/**
 * @jest-environment jsdom
 * @jest-environment-options {"url": "https://news.example/world/launch-video"}
 */
import { MediaOverlay } from '../src/overlay';

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
