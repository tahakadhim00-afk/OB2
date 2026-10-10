/**
 * @jest-environment jsdom
 * @jest-environment-options {"url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=42"}
 */
import { isMediaCandidate, isYouTubeHost, mediaLabel, youTubeVideoPage } from '../src/media';
import { MediaOverlay } from '../src/overlay';
import { formatBytes } from '../src/format';

describe('YouTube addresses (D-71)', () => {
  it.each([
    ['https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1', 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'],
    ['https://youtu.be/dQw4w9WgXcQ?si=x', 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'],
    ['https://www.youtube.com/shorts/aBcDeFgHiJ0', 'https://www.youtube.com/watch?v=aBcDeFgHiJ0'],
    ['https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ', 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'],
    ['https://music.youtube.com/watch?v=dQw4w9WgXcQ', 'https://music.youtube.com/watch?v=dQw4w9WgXcQ'],
  ])('%s is one video: %s', (input, expected) => {
    expect(youTubeVideoPage(input)).toBe(expected);
  });

  it.each(['https://www.youtube.com/', 'https://www.youtube.com/@channel', 'https://www.youtube.com/watch?v=short', 'https://example.com/watch?v=dQw4w9WgXcQ'])(
    '%s is not',
    (input) => expect(youTubeVideoPage(input)).toBeUndefined(),
  );

  it('knows YouTube pages that are not one video', () => {
    expect(isYouTubeHost('https://www.youtube.com/feed/subscriptions')).toBe(true);
    expect(isYouTubeHost('https://vimeo.com/1')).toBe(false);
  });

  it('labels and accepts a video page entry', () => {
    expect(mediaLabel({ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', page: true }, formatBytes)).toBe('Choose quality… (whole video)');
    expect(isMediaCandidate({ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', page: true })).toBe(true);
    expect(isMediaCandidate({ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', page: 'yes' })).toBe(false);
  });
});

describe('button on a YouTube watch page', () => {
  it('offers the video page itself, whatever the player loaded', () => {
    const send = jest.fn().mockResolvedValue('sent');
    const overlay = new MediaOverlay(document, send, 'open');
    const video = document.createElement('video');
    video.setAttribute('src', 'blob:https://www.youtube.com/0f1e');
    video.getBoundingClientRect = () => ({ left: 0, top: 0, right: 854, bottom: 480, width: 854, height: 480, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
    document.body.append(video);
    overlay.addDetected({ url: 'https://rr1.googlevideo.com/videoplayback?id=1&itag=18', mime: 'video/mp4', size: 5_000_000 });

    expect(overlay.offersFor(video)).toEqual([{ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', page: true }]);
    expect(overlay.show(video)).toBe(true);

    const host = document.querySelector('ob2-media-button')!;
    (host.shadowRoot!.querySelector('.main') as HTMLButtonElement).click();
    expect(send).toHaveBeenCalledWith({ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', page: true });
  });
});
