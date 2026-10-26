/**
 * @jest-environment jsdom
 */
import { MediaOverlay, type SendResult } from '../src/overlay';

// jsdom has no layout, so each video gets the box a browser would give it.
function addVideo(doc: Document, attrs: Record<string, string>, box = { left: 0, top: 0, width: 640, height: 360 }): HTMLVideoElement {
  const video = doc.createElement('video');
  for (const [name, value] of Object.entries(attrs)) video.setAttribute(name, value);
  const rect = { ...box, right: box.left + box.width, bottom: box.top + box.height, x: box.left, y: box.top };
  video.getBoundingClientRect = () => ({ ...rect, toJSON: () => rect }) as DOMRect;
  doc.body.append(video);
  return video;
}

let doc: Document;
let send: jest.Mock<Promise<SendResult>, [unknown]>;
let overlay: MediaOverlay;

const host = () => doc.querySelector('ob2-media-button') as HTMLElement;
const shadow = () => host().shadowRoot!;
const mainButton = () => shadow().querySelector('.main') as HTMLButtonElement;
const label = () => mainButton().textContent;
const isShown = () => !!doc.querySelector('ob2-media-button') && host().style.display === 'block';

beforeEach(() => {
  jest.useFakeTimers();
  doc = document.implementation.createHTMLDocument('page');
  send = jest.fn().mockResolvedValue('sent');
  overlay = new MediaOverlay(doc, send, 'open');
  overlay.attach();
});

afterEach(() => jest.useRealTimers());

describe('button over videos (FR-24)', () => {
  it("offers the video's own file and reports the result", async () => {
    const video = addVideo(doc, { src: 'https://cdn.e.com/clip.mp4' });

    expect(overlay.show(video)).toBe(true);
    expect(label()).toBe('Download with OB2');
    expect(host().style.position).toBe('fixed');

    mainButton().click();
    await Promise.resolve();
    await Promise.resolve();

    expect(send).toHaveBeenCalledWith({ url: 'https://cdn.e.com/clip.mp4' });
    expect(label()).toBe('Sent to OB2');
    jest.advanceTimersByTime(2500);
    expect(isShown()).toBe(false);
  });

  it('says so when OB2 could not be reached', async () => {
    send.mockResolvedValue('browser');
    overlay.show(addVideo(doc, { src: 'https://e.com/a.webm' }));

    mainButton().click();
    await Promise.resolve();
    await Promise.resolve();

    expect(label()).toContain('the browser is downloading it');
  });

  it('for a blob: video, offers what the frame loaded, newest first, in a menu', async () => {
    const video = addVideo(doc, { src: 'blob:https://e.com/0f1e' });
    expect(overlay.show(video)).toBe(false); // nothing seen yet: no button

    overlay.addDetected({ url: 'https://cdn.e.com/old.mp4', size: 10 * 1024 * 1024 });
    overlay.addDetected({ url: 'https://cdn.e.com/new.mp4', mime: 'video/mp4' });
    expect(overlay.show(video)).toBe(true);
    expect(label()).toBe('Download with OB2 (2)');

    mainButton().click();
    const items = Array.from(shadow().querySelectorAll<HTMLButtonElement>('.item'));
    expect(items.map((i) => i.textContent)).toEqual(['new.mp4 · video', 'old.mp4 · 10 MB']);
    expect(send).not.toHaveBeenCalled();

    items[1].click();
    expect(send).toHaveBeenCalledWith({ url: 'https://cdn.e.com/old.mp4', size: 10 * 1024 * 1024 });
  });

  it('shows nothing for small videos, streams it cannot download, or when switched off', () => {
    expect(overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }, { left: 0, top: 0, width: 120, height: 90 }))).toBe(false);
    expect(overlay.show(addVideo(doc, { src: 'https://e.com/live/index.m3u8' }))).toBe(false);

    overlay.enabled = false;
    expect(overlay.show(addVideo(doc, { src: 'https://e.com/b.mp4' }))).toBe(false);
  });

  it('appears while the pointer is over a video and goes away after it leaves', () => {
    const video = addVideo(doc, { src: 'https://e.com/a.mp4' }, { left: 100, top: 100, width: 640, height: 360 });
    const move = (x: number, y: number) => doc.dispatchEvent(new MouseEvent('pointermove', { clientX: x, clientY: y, bubbles: true }));

    move(300, 200);
    jest.advanceTimersToNextFrame();
    expect(overlay.visibleFor).toBe(video);
    expect(isShown()).toBe(true);
    expect(host().style.top).toBe('108px');

    move(10, 10);
    jest.advanceTimersToNextFrame();
    expect(isShown()).toBe(true); // a short grace period to reach the button
    jest.advanceTimersByTime(1200);
    expect(isShown()).toBe(false);
  });

  it('appears for a few seconds when a video starts playing', () => {
    const video = addVideo(doc, { src: 'https://e.com/a.mp4' });

    video.dispatchEvent(new Event('play'));
    expect(isShown()).toBe(true);
    jest.advanceTimersByTime(4000);
    expect(isShown()).toBe(false);
  });

  it('keeps clicks on the button away from the page', () => {
    const pageClick = jest.fn();
    doc.addEventListener('click', pageClick);
    overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }));

    mainButton().click();

    expect(pageClick).not.toHaveBeenCalled();
  });
});

describe('moving the button with the pointer (D-81)', () => {
  // A 640×360 video at (100, 100): the button's corner may go from (108, 108) to (732, 452).
  const box = { left: 100, top: 100, width: 640, height: 360 };
  const press = (type: string, x: number, y: number) => mainButton().dispatchEvent(new MouseEvent(type, { clientX: x, clientY: y, button: 0, bubbles: true }));
  const place = () => ['left', 'top', 'right', 'bottom'].map((side) => host().style.getPropertyValue(side));
  const wrap = () => shadow().querySelector('.wrap')!;

  it('sits in the top right corner until it is moved', () => {
    overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }, box));

    expect(place()).toEqual(['auto', '108px', '8px', 'auto']);
  });

  it('follows a drag, keeps the new place, and the release is not a click', async () => {
    const moved = jest.fn();
    overlay.onMoved = moved;
    overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }, box));

    press('pointerdown', 720, 120);
    press('pointermove', 722, 121); // a hand's tremble is not a drag yet
    expect(place()).toEqual(['auto', '108px', '8px', 'auto']);
    press('pointermove', 320, 400);
    press('pointerup', 320, 400);
    mainButton().click(); // the browser's click after the release

    // From (732, 108) by (-400, +280): the lower left part, so the menu opens upwards and to the right.
    expect(place()).toEqual(['332px', 'auto', 'auto', '72px']);
    expect(wrap().classList.contains('left')).toBe(true);
    expect(wrap().classList.contains('up')).toBe(true);
    expect(moved).toHaveBeenCalledTimes(1);
    expect(moved).toHaveBeenCalledWith({ x: 224 / 624, y: 280 / 344 });
    expect(send).not.toHaveBeenCalled();

    mainButton().click(); // a real click afterwards still downloads
    await Promise.resolve();
    expect(send).toHaveBeenCalledWith({ url: 'https://e.com/a.mp4' });
  });

  it('a press that barely moves is still a click', async () => {
    const moved = jest.fn();
    overlay.onMoved = moved;
    overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }, box));

    press('pointerdown', 720, 120);
    press('pointermove', 723, 122);
    press('pointerup', 723, 122);
    mainButton().click();
    await Promise.resolve();

    expect(send).toHaveBeenCalledTimes(1);
    expect(moved).not.toHaveBeenCalled();
  });

  it('stays over the video however far it is dragged', () => {
    overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }, box));

    press('pointerdown', 720, 120);
    press('pointermove', -500, 2000);
    press('pointerup', -500, 2000);

    expect(overlay.position).toEqual({ x: 0, y: 1 });
    expect(place()).toEqual(['108px', 'auto', 'auto', '8px']);
  });

  it('keeps its place over the next video, in proportion, and closes the menu while it moves', () => {
    overlay.position = { x: 0, y: 1 };
    overlay.show(addVideo(doc, { src: 'https://e.com/a.mp4' }, box));
    expect(place()).toEqual(['108px', 'auto', 'auto', '8px']);
    overlay.hide();

    const fullscreen = addVideo(doc, { src: 'blob:https://e.com/1' }, { left: 0, top: 0, width: 1280, height: 720 });
    overlay.addDetected({ url: 'https://cdn.e.com/a.mp4' });
    overlay.addDetected({ url: 'https://cdn.e.com/b.mp4' });
    overlay.show(fullscreen);
    expect(place()).toEqual(['8px', 'auto', 'auto', '8px']);

    mainButton().click();
    expect(shadow().querySelector<HTMLUListElement>('.menu')!.hidden).toBe(false);
    press('pointerdown', 20, 700);
    press('pointermove', 640, 360);
    expect(shadow().querySelector<HTMLUListElement>('.menu')!.hidden).toBe(true);
  });
});

describe('button fallback: addresses in the page code (D-70)', () => {
  it('offers a video address written in a script when nothing else is known', () => {
    doc.body.insertAdjacentHTML('beforeend', '<script>cfg = {"file":"https:\\/\\/cdn.e.com\\/movie.mp4"}</script>');
    const video = addVideo(doc, { src: 'blob:https://e.com/9' });

    expect(overlay.show(video)).toBe(true);
    mainButton().click();

    expect(send).toHaveBeenCalledWith({ url: 'https://cdn.e.com/movie.mp4' });
  });

  it("doesn't offer another video's file", () => {
    addVideo(doc, { src: 'https://e.com/other.mp4' });
    const blobVideo = addVideo(doc, { src: 'blob:https://e.com/9' });

    expect(overlay.show(blobVideo)).toBe(false);
  });
});

describe('context menu on a media element', () => {
  it('resolves a blob: element to the newest file the frame loaded', () => {
    const video = addVideo(doc, { src: 'blob:https://e.com/1' });
    overlay.addDetected({ url: 'https://e.com/a.mp4' });
    overlay.addDetected({ url: 'https://e.com/b.mp4' });

    video.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, composed: true }));

    expect(overlay.mediaForContextMenu()).toEqual({ url: 'https://e.com/b.mp4' });
  });

  it("prefers the element's own file", () => {
    const audio = doc.createElement('audio');
    audio.setAttribute('src', 'https://e.com/song.mp3');
    doc.body.append(audio);
    overlay.addDetected({ url: 'https://e.com/other.mp4' });

    audio.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, composed: true }));

    expect(overlay.mediaForContextMenu()).toEqual({ url: 'https://e.com/song.mp3' });
  });
});
