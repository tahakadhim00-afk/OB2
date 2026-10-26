/**
 * @jest-environment jsdom
 * @jest-environment-options {"url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=42"}
 */
import type { FormatsOutcome } from '../src/capture';
import {
  isMediaCandidate, isMediaChoice, isMediaOption, isYouTubeHost, mediaLabel, mediaSubtitleOf, optionLabel, subtitleLabel, youTubeVideoPage, type StreamInfo,
} from '../src/media';
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
    host.remove();
    video.remove();
  });
});

const WATCH = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ';
const LISTED: FormatsOutcome = {
  action: 'listed',
  title: 'Launch day / highlights',
  options: [
    { format: '313+251', container: 'webm', audioOnly: false, quality: '2160p', codec: 'VP9', size: 483_500_000 },
    { format: '137+140', container: 'mp4', audioOnly: false, quality: '1080p', codec: 'H.264', size: 108_400_000 },
    { format: '18', container: 'mp4', audioOnly: false, quality: '360p', codec: 'H.264' },
    { format: '140', container: 'm4a', audioOnly: true, quality: '130 kbit/s', codec: 'AAC', size: 3_400_000 },
  ],
};

describe('quality menu on a YouTube watch page (D-72)', () => {
  let send: jest.Mock;
  let listFormats: jest.Mock<Promise<FormatsOutcome>, [string, readonly StreamInfo[]]>;
  let overlay: MediaOverlay;
  let video: HTMLVideoElement;

  const shadow = () => document.querySelector('ob2-media-button')!.shadowRoot!;
  const main = () => shadow().querySelector('.main') as HTMLButtonElement;
  const menu = () => shadow().querySelector('.menu') as HTMLUListElement;
  const items = () => Array.from(shadow().querySelectorAll<HTMLButtonElement>('.item'));
  const notes = () => Array.from(shadow().querySelectorAll('.note'), (n) => n.textContent);
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

  beforeEach(() => {
    send = jest.fn().mockResolvedValue('sent');
    listFormats = jest.fn().mockResolvedValue(LISTED);
    overlay = new MediaOverlay(document, send, 'open', listFormats);
    video = document.createElement('video');
    video.setAttribute('src', 'blob:https://www.youtube.com/0f1e');
    video.getBoundingClientRect = () => ({ left: 0, top: 0, right: 854, bottom: 480, width: 854, height: 480, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
    document.body.append(video);
    overlay.show(video);
  });

  afterEach(() => {
    overlay.hide();
    document.querySelector('ob2-media-button')?.remove();
    video.remove();
  });

  it('lists every quality OB2 reads, and a picked one goes to OB2 as that quality', async () => {
    expect(main().getAttribute('aria-haspopup')).toBe('menu');

    main().click();
    expect(listFormats).toHaveBeenCalledWith(WATCH, []); // D-79: yt-dlp always reads YouTube itself
    expect(notes()).toEqual(['Getting the qualities from OB2…']);
    expect(menu().getAttribute('aria-busy')).toBe('true');
    expect(send).not.toHaveBeenCalled();

    await settle();
    expect(menu().hasAttribute('aria-busy')).toBe(false);
    expect(items().map((i) => i.textContent)).toEqual([
      '2160p · WEBM (VP9) · 461 MB',
      '1080p · MP4 (H.264) · 103 MB',
      '360p · MP4 (H.264)',
      'Audio only · M4A (AAC) · 130 kbit/s · 3.2 MB',
      'More choices in OB2… (audio track, folder)',
    ]);

    items()[1].click();
    expect(send).toHaveBeenCalledWith({ url: WATCH, page: true, format: '137+140', container: 'mp4', name: 'Launch day / highlights', size: 108_400_000 });
  });

  it('leaves other pairs to the Download Video window', async () => {
    main().click();
    await settle();

    items().at(-1)!.click();
    expect(send).toHaveBeenCalledWith({ url: WATCH, page: true });
  });

  it('asks OB2 once while the list is fresh', async () => {
    main().click();
    await settle();
    main().click(); // closes
    expect(menu().hidden).toBe(true);
    main().click();
    await settle();

    expect(listFormats).toHaveBeenCalledTimes(1);
    expect(items()).toHaveLength(5);
  });

  it('offers to set the tools up when OB2 has none yet', async () => {
    listFormats.mockResolvedValueOnce({ action: 'failed', reason: 'tools-missing' });

    main().click();
    await settle();
    expect(notes()).toEqual(['OB2 needs its video tools (yt-dlp, ffmpeg, Deno) first.']);
    expect(items().map((i) => i.textContent)).toEqual(['Set up video downloads in OB2…']);

    items()[0].click();
    expect(send).toHaveBeenCalledWith({ url: WATCH, page: true });
  });

  it("shows yt-dlp's reason, or that OB2 can't be reached, and asks again next time", async () => {
    listFormats.mockResolvedValueOnce({ action: 'failed', reason: 'media-failed', message: "Sign in to confirm you're not a bot" });
    main().click();
    await settle();
    expect(notes()).toEqual(["Sign in to confirm you're not a bot"]);
    expect(items().map((i) => i.textContent)).toEqual(['Open in OB2…']);

    main().click();
    listFormats.mockResolvedValueOnce({ action: 'failed', reason: 'unreachable' });
    main().click();
    await settle();
    expect(listFormats).toHaveBeenCalledTimes(2);
    expect(notes()).toEqual(['OB2 not reachable. Is it set up? (OB2 popup)']);
    expect(items()).toHaveLength(0);
  });

  it('lists the subtitle tracks with a button per format, each a download of its own (D-74)', async () => {
    listFormats.mockResolvedValueOnce({
      ...LISTED,
      subtitles: [
        { format: 'subs:en', language: 'en', name: 'English', automatic: false, containers: ['srt', 'ass', 'vtt'] },
        { format: 'auto-subs:en-orig', language: 'en-orig', name: 'English (Original)', automatic: true, containers: ['srt', 'ass', 'vtt'] },
      ],
    });

    main().click();
    await settle();
    const rows = Array.from(shadow().querySelectorAll('.sub'));
    expect(notes()).toEqual(['Subtitles']);
    expect(rows.map((r) => r.querySelector('.name')!.textContent)).toEqual(['English', 'English (Original) · auto-generated']);
    expect(Array.from(rows[0].querySelectorAll('.fmt'), (b) => b.textContent)).toEqual(['SRT', 'ASS', 'VTT']);
    expect(items().at(-1)!.textContent).toBe('More choices in OB2… (audio track, folder)'); // still last

    const ass = rows[1].querySelectorAll<HTMLButtonElement>('.fmt')[1];
    expect(ass.getAttribute('aria-label')).toBe('English (Original) · auto-generated subtitles as ASS');
    ass.click();
    expect(send).toHaveBeenCalledWith({ url: WATCH, page: true, format: 'auto-subs:en-orig', container: 'ass', name: 'Launch day / highlights' });
  });

  it('says so when the video has no subtitles', async () => {
    listFormats.mockResolvedValueOnce({ ...LISTED, subtitles: [] });

    main().click();
    await settle();

    expect(notes()).toEqual(['Subtitles: none on this video']);
  });

  it('shows no subtitle section when OB2 predates subtitles', async () => {
    main().click(); // LISTED has no subtitles field
    await settle();

    expect(notes()).toEqual([]);
    expect(shadow().querySelectorAll('.sub')).toHaveLength(0);
  });

  it('drops an answer that comes after the menu was closed', async () => {
    let answer!: (o: FormatsOutcome) => void;
    listFormats.mockReturnValueOnce(new Promise((resolve) => (answer = resolve)));

    main().click();
    main().click(); // closed while OB2 is still reading the page
    answer(LISTED);
    await settle();

    expect(menu().hidden).toBe(true);
    expect(items()).toHaveLength(0);
  });
});

describe('quality entries (D-72)', () => {
  it('reads like a choice of file', () => {
    expect(optionLabel({ format: '299+140', container: 'mp4', audioOnly: false, quality: '1080p50', codec: 'H.264', size: 163_400_000 }, formatBytes)).toBe('1080p50 · MP4 (H.264) · 156 MB');
    expect(optionLabel({ format: '140-1', container: 'm4a', audioOnly: true, quality: '129 kbit/s', codec: 'AAC', track: 'Spanish' }, formatBytes)).toBe('Audio only (Spanish) · M4A (AAC) · 129 kbit/s');
    expect(optionLabel({ format: 'hls-720', container: 'mp4', audioOnly: false }, formatBytes)).toBe('Video · MP4');
  });

  it('accepts only plain format ids from OB2', () => {
    expect(isMediaOption({ format: '137+140', container: 'mp4', audioOnly: false })).toBe(true);
    expect(isMediaOption({ format: 'bestvideo+bestaudio/best', container: 'mp4', audioOnly: false })).toBe(false);
    expect(isMediaOption({ format: '137', container: '../mp4', audioOnly: false })).toBe(false);
    expect(isMediaOption({ format: '137', container: 'mp4' })).toBe(false);
  });

  it('takes subtitle tracks only as srt, ass or vtt, with plain language keys (D-74)', () => {
    expect(isMediaChoice('subs:en', 'srt')).toBe(true);
    expect(isMediaChoice('auto-subs:pt-BR', 'vtt')).toBe(true);
    expect(isMediaChoice('subs:en', 'mp4')).toBe(false);
    expect(isMediaChoice('subs:en.*', 'srt')).toBe(false); // yt-dlp reads --sub-langs as patterns
    expect(isMediaChoice('subs:en,fr', 'srt')).toBe(false);
    expect(isMediaChoice('subs:-en', 'srt')).toBe(false);

    const track = { format: 'subs:en', language: 'en', name: ' English ', automatic: false, containers: ['srt', 'txt', 'ass'] };
    expect(mediaSubtitleOf(track)).toEqual({ ...track, name: 'English', containers: ['srt', 'ass'] });
    expect(mediaSubtitleOf({ ...track, containers: ['txt'] })).toBeUndefined();
    expect(mediaSubtitleOf({ ...track, format: '137' })).toBeUndefined();
    expect(mediaSubtitleOf({ ...track, automatic: 'no' })).toBeUndefined();
    expect(subtitleLabel({ ...track, name: 'Arabic', automatic: true })).toBe('Arabic · auto-generated');
    expect(isMediaCandidate({ url: WATCH, page: true, format: 'subs:en', container: 'srt', name: 'Clip' })).toBe(true);
  });

  it('lets a picked quality through only on a video page entry', () => {
    expect(isMediaCandidate({ url: WATCH, page: true, format: '137+140', container: 'mp4', name: 'Clip' })).toBe(true);
    expect(isMediaCandidate({ url: WATCH, format: '137+140', container: 'mp4' })).toBe(false);
    expect(isMediaCandidate({ url: WATCH, page: true, format: '137+140' })).toBe(false);
    expect(isMediaCandidate({ url: WATCH, page: true, container: 'mp4' })).toBe(false);
    expect(isMediaCandidate({ url: WATCH, page: true, format: '-f', container: 'mp4' })).toBe(false);
  });
});
