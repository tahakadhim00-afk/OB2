// FR-24: the "Download with OB2" button that floats over a video while the pointer is on it, and
// for a few seconds after a video starts playing. It offers the video's own file when
// the element plays an http(s) address, else the media files the background saw this frame load
// (players that feed the element a blob: URL). The button lives in a closed shadow root so page
// CSS and scripts can't restyle or read it, and it is built without innerHTML (Trusted Types).
// D-72: on a video page (YouTube) a click lists the qualities OB2 reads with yt-dlp, and each
// entry downloads that quality. D-74: then the video's subtitle tracks, each with a button per
// format (SRT, ASS, VTT) that downloads it as a file of its own. D-79: the click also names the
// HLS and DASH manifests this page's player loaded, which OB2 reads when yt-dlp doesn't know the
// site; the entries then download that stream. D-81: the button can be dragged anywhere over the
// video; the place is kept per site (the content script saves it).

import type { FormatsFailure, FormatsOutcome } from './capture';
import { formatBytes } from './format';
import {
  isDownloadableMediaUrl, isYouTubeHost, mediaLabel, optionLabel, subtitleLabel, youTubeVideoPage, type MediaCandidate, type RequestHeaders, type StreamInfo,
} from './media';
import type { MediaSubtitle } from './protocol';
import { scanPage } from './scan';

export type SendResult = 'sent' | 'browser' | 'unreachable' | 'incognito' | 'failed';

const MIN_WIDTH = 200;
const MIN_HEIGHT = 100;
const INSET = 8;
const HIDE_DELAY_MS = 1200;
const PLAY_SHOW_MS = 4000;
const RESULT_SHOW_MS = 2500;
const MAX_DETECTED = 30;
/** The page-code fallback rescans at most this often while the pointer moves over videos. */
const SCAN_REUSE_MS = 5000;
/** A video page's qualities are asked for again after this long (D-72). */
const FORMATS_REUSE_MS = 5 * 60_000;
/** D-79: manifests kept per frame, and the most one request names (the app takes no more). */
const MAX_STREAMS = 20;
const STREAMS_ASKED = 5;
/** D-79: an encrypted player (Netflix and the like). Nothing can download what it plays. */
const PROTECTED_TEXT = 'This video is copy-protected (DRM), so OB2 cannot download it.';
/** D-81: a press that moves further than this is a drag, not a click. */
const DRAG_THRESHOLD = 5;

/**
 * D-81: where the button sits over the video, as fractions of the room it can move in: 0 is the
 * left (top) edge, 1 the right (bottom) one. Fractions, not pixels, so the place holds when the
 * video grows to fullscreen or shrinks back.
 */
export interface ButtonPosition {
  x: number;
  y: number;
}

/** The top right corner, where the button sits until it is moved. */
export const DEFAULT_POSITION: ButtonPosition = { x: 1, y: 0 };

const RESULT_TEXT: Record<SendResult, string> = {
  sent: 'Sent to OB2',
  browser: 'OB2 not reachable: the browser is downloading it',
  unreachable: 'OB2 not reachable. Is it set up? (OB2 popup)',
  incognito: 'Not available in Incognito',
  failed: 'Could not send to OB2',
};

const FORMATS_TEXT: Record<FormatsFailure, string> = {
  unreachable: RESULT_TEXT.unreachable,
  'tools-missing': 'OB2 needs its video tools (yt-dlp, ffmpeg, Deno) first.',
  'media-failed': 'OB2 could not read this video.',
  timeout: 'OB2 took too long to answer. Try again in a moment.',
  incognito: RESULT_TEXT.incognito,
  failed: 'Could not get the qualities from OB2.',
};

const CSS = `
:host { all: initial; }
.wrap { font: 13px/1.3 "Segoe UI", system-ui, sans-serif; display: flex; flex-direction: column; align-items: flex-end; }
.wrap.left { align-items: flex-start; }
.wrap.up { flex-direction: column-reverse; }
.wrap.up .menu { margin: 0 0 4px; }
.main { display: inline-flex; align-items: center; gap: 6px; padding: 6px 10px; border: 0; border-radius: 4px;
  background: #1a73e8; color: #fff; font: inherit; cursor: pointer; box-shadow: 0 1px 4px rgba(0, 0, 0, .45);
  touch-action: none; user-select: none; }
.main.dragging { cursor: grabbing; }
.main:hover { background: #1765cc; }
.main:focus-visible { outline: 2px solid #fff; outline-offset: -4px; }
.main:disabled { cursor: default; background: #3c4043; }
.main svg { width: 16px; height: 16px; flex: none; }
.menu { list-style: none; margin: 4px 0 0; padding: 4px 0; min-width: 220px; max-width: 360px; max-height: 320px; overflow: auto;
  background: #fff; color: #202124; border-radius: 4px; box-shadow: 0 2px 8px rgba(0, 0, 0, .35); }
.menu[hidden] { display: none; }
.item { display: block; width: 100%; box-sizing: border-box; padding: 6px 10px; border: 0; background: none; color: inherit;
  font: inherit; text-align: left; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.item:hover, .item:focus-visible { background: #e8f0fe; outline: none; }
.item.more { border-top: 1px solid #dadce0; color: #1a73e8; }
.note { padding: 6px 10px; color: #5f6368; white-space: normal; overflow-wrap: anywhere; }
.note.heading { border-top: 1px solid #dadce0; padding-bottom: 2px; font-size: 12px; }
.sub { display: flex; align-items: center; gap: 4px; padding: 2px 8px 2px 10px; }
.sub .name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.fmt { flex: none; padding: 2px 6px; border: 1px solid #dadce0; border-radius: 3px; background: none; color: #1a73e8;
  font: inherit; font-size: 12px; cursor: pointer; }
.fmt:hover, .fmt:focus-visible { background: #e8f0fe; outline: none; }
`;

/** The element's own http(s) source: what it plays now, else its src or <source> children. */
function ownSources(media: HTMLMediaElement): string[] {
  if (media.currentSrc) {
    // A blob: or a streaming manifest: only what the network showed can be downloaded.
    return isDownloadableMediaUrl(media.currentSrc) ? [media.currentSrc] : [];
  }
  const urls = [media.getAttribute('src') ? media.src : '', ...Array.from(media.querySelectorAll('source'), (s) => s.src)];
  return [...new Set(urls.filter((u) => isDownloadableMediaUrl(u)))];
}

/** What the button offers for this element, best first. */
function candidatesFor(media: HTMLMediaElement, detected: readonly MediaCandidate[]): MediaCandidate[] {
  const own = ownSources(media);
  if (own.length > 0) return own.map((url) => detected.find((d) => d.url === url) ?? { url });
  return [...detected].reverse(); // newest first: most likely the video now on screen
}

interface Ui {
  host: HTMLElement;
  wrap: HTMLDivElement;
  main: HTMLButtonElement;
  label: HTMLSpanElement;
  menu: HTMLUListElement;
}

/** D-81: a press on the button, until it is known to be a click or a drag. */
interface Drag {
  pointerId?: number;
  startX: number;
  startY: number;
  /** The button's top left corner when the press began. */
  left: number;
  top: number;
  moving: boolean;
}

export class MediaOverlay {
  enabled = true;
  /** D-81: where the button sits over the video; the content script sets the one saved for this site. */
  position: ButtonPosition = DEFAULT_POSITION;
  /** D-81: told the new place after the user dragged the button, to keep it for this site. */
  onMoved?: (position: ButtonPosition) => void;
  private drag?: Drag;
  /** D-81: the click a browser sends when a drag ends is not a click on the button. */
  private dragEnded = false;
  private readonly detected: MediaCandidate[] = [];
  /** D-79: manifests in the order the player loaded them, each with the page it was on. D-80: and the headers the browser sent. */
  private readonly streams: Array<StreamInfo & { page: string }> = [];
  private ui?: Ui;
  private target?: HTMLVideoElement;
  private candidates: MediaCandidate[] = [];
  private pointer = { x: -1, y: -1 };
  private frame = 0;
  private hideTimer?: ReturnType<typeof setTimeout>;
  private busy = false;
  private contextMedia?: HTMLMediaElement;
  private pageScan?: { at: number; media: MediaCandidate[] };
  private readonly formats = new Map<string, { at: number; outcome: Promise<FormatsOutcome> }>();
  /** The video page whose qualities the open menu shows (or is waiting for). */
  private menuPage?: string;

  /**
   * `shadowMode` is open only in tests, which need to reach the button. Without `listFormats`
   * a video page goes to OB2's Download Video window as it is.
   */
  constructor(
    private readonly doc: Document,
    private readonly send: (media: MediaCandidate) => Promise<SendResult>,
    private readonly shadowMode: ShadowRootMode = 'closed',
    private readonly listFormats?: (pageUrl: string, streams: readonly StreamInfo[]) => Promise<FormatsOutcome>,
  ) {}

  attach(): void {
    const passive = { capture: true, passive: true };
    this.doc.addEventListener('pointermove', (e) => this.onPointerMove(e), passive);
    this.doc.addEventListener('pointerdown', (e) => this.onPointerDown(e), passive);
    // Media events don't bubble, but they do pass through the capture phase.
    this.doc.addEventListener('play', (e) => this.onPlay(e), true);
    this.doc.addEventListener('scroll', () => this.reposition(), passive);
    this.doc.defaultView?.addEventListener('resize', () => this.reposition(), { passive: true });
    this.doc.addEventListener('fullscreenchange', () => this.onFullscreenChange());
    this.doc.addEventListener('contextmenu', (e) => (this.contextMedia = mediaInPath(e) ?? this.videoAt(e.clientX, e.clientY)), passive);
  }

  /** A media file the background saw this frame load. */
  addDetected(media: MediaCandidate): void {
    const index = this.detected.findIndex((d) => d.url === media.url);
    if (index >= 0) {
      this.detected[index] = { ...this.detected[index], ...media };
    } else {
      this.detected.push(media);
      if (this.detected.length > MAX_DETECTED) this.detected.shift();
    }
    if (this.target && !this.busy) this.show(this.target); // refresh the count on the button
  }

  /** What the background saw this frame load, oldest first. */
  detectedMedia(): readonly MediaCandidate[] {
    return this.detected;
  }

  /** D-79: an HLS or DASH manifest the background saw this frame load. D-80: with the headers the browser sent for it. */
  addStream(url: string, headers: RequestHeaders = {}): void {
    const known = this.streams.find((s) => s.url === url);
    if (known) {
      known.headers = headers; // a playlist loaded again carries the player's latest token
      return;
    }
    this.streams.push({ url, headers, page: withoutHash(this.doc.URL) });
    if (this.streams.length > MAX_STREAMS) this.streams.shift();
  }

  /**
   * D-79: the manifests loaded on the page now shown, oldest first: a player loads the master
   * playlist (every quality) before the one quality it plays. One that played on another page of
   * a single-page site belongs to another video. None on YouTube, which yt-dlp always reads.
   */
  streamsForPage(): StreamInfo[] {
    const page = withoutHash(this.doc.URL);
    if (isYouTubeHost(page)) return [];
    return this.streams.filter((s) => s.page === page).slice(0, STREAMS_ASKED).map(({ url, headers }) => ({ url, headers }));
  }

  /** For "Download media with OB2" on an element whose src Chrome can't hand over (blob:). */
  mediaForContextMenu(): MediaCandidate | undefined {
    const media = this.contextMedia ?? this.doc.querySelector('video') ?? undefined;
    return media ? this.offersFor(media)[0] : (this.detected.at(-1) ?? this.mediaInPageCode()[0]);
  }

  /**
   * Everything the button offers for this element, best first. On a YouTube video page that is
   * the page itself (D-71). Elsewhere: the element's own file, else the files this frame loaded,
   * else addresses in the page code (D-70); and when the element plays a blob: or a stream, the
   * page last, for OB2's quality chooser.
   */
  offersFor(media: HTMLMediaElement): MediaCandidate[] {
    const page = this.doc.URL;
    const youTube = youTubeVideoPage(page);
    if (youTube) return [{ url: youTube, page: true }];
    let offers = candidatesFor(media, this.detected);
    if (offers.length === 0) offers = this.mediaInPageCode();
    if (ownSources(media).length === 0 && /^https?:/i.test(page) && !isYouTubeHost(page)) {
      offers = [...offers, { url: page, page: true }];
    }
    return offers;
  }

  /** Shows the button over this video. False when it isn't big enough or there is nothing to offer. */
  show(video: HTMLVideoElement): boolean {
    if (!this.enabled || !isLargeEnough(video.getBoundingClientRect())) return false;
    const candidates = this.offersFor(video);
    if (candidates.length === 0) return false;
    const ui = (this.ui ??= this.build());
    this.target = video;
    this.candidates = candidates;
    this.cancelHide();
    this.placeHost();
    if (!this.busy) this.setLabel(candidates.length > 1 ? `Download with OB2 (${candidates.length})` : 'Download with OB2');
    ui.main.setAttribute('aria-haspopup', candidates.length > 1 || this.choosesQuality(candidates[0]) ? 'menu' : 'false');
    setDisplay(ui.host, true);
    this.reposition();
    return true;
  }

  hide(): void {
    this.cancelHide();
    this.target = undefined;
    this.menuPage = undefined;
    this.drag = undefined;
    if (this.ui) {
      setDisplay(this.ui.host, false);
      this.ui.menu.hidden = true;
    }
  }

  get visibleFor(): HTMLVideoElement | undefined {
    return this.target;
  }

  /** Video and audio files whose addresses are written in the page (links, scripts, meta tags). */
  private mediaInPageCode(): MediaCandidate[] {
    const now = Date.now();
    if (!this.pageScan || now - this.pageScan.at > SCAN_REUSE_MS) {
      // A file another <video>/<audio> on the page plays belongs to that element, not this one.
      const elsewhere = new Set(Array.from(this.doc.querySelectorAll<HTMLMediaElement>('video, audio')).flatMap(ownSources));
      const links = scanPage(this.doc).links.filter((l) => (l.kind === 'video' || l.kind === 'audio') && !elsewhere.has(l.url));
      this.pageScan = { at: now, media: links.map((l) => ({ url: l.url })) };
    }
    return this.pageScan.media;
  }

  private onPointerMove(event: PointerEvent): void {
    if (this.ui && event.composedPath().includes(this.ui.host)) {
      this.cancelHide();
      return;
    }
    this.pointer = { x: event.clientX, y: event.clientY };
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.update();
    });
  }

  private onPointerDown(event: PointerEvent): void {
    if (this.ui && !this.ui.menu.hidden && !event.composedPath().includes(this.ui.host)) this.hide();
  }

  private onPlay(event: Event): void {
    const video = event.target as Element | null;
    if (video?.localName !== 'video' || this.busy || this.menuOpen()) return;
    if (this.target && this.target !== video) return; // don't take the button from the hovered video
    if (this.show(video as HTMLVideoElement)) this.scheduleHide(PLAY_SHOW_MS);
  }

  private update(): void {
    if (this.busy || this.menuOpen()) return;
    const video = this.videoAt(this.pointer.x, this.pointer.y);
    if (video && this.show(video)) return;
    if (this.target) this.scheduleHide(HIDE_DELAY_MS);
  }

  /** The smallest video under the point: player overlays cover the element, so no hit-testing. */
  private videoAt(x: number, y: number): HTMLVideoElement | undefined {
    let best: HTMLVideoElement | undefined;
    let bestArea = Infinity;
    for (const video of Array.from(this.doc.getElementsByTagName('video'))) {
      const r = video.getBoundingClientRect();
      if (!isLargeEnough(r) || x < r.left || x > r.right || y < r.top || y > r.bottom) continue;
      if (r.width * r.height < bestArea) {
        best = video;
        bestArea = r.width * r.height;
      }
    }
    return best;
  }

  private reposition(): void {
    if (!this.ui || !this.target) return;
    const rect = this.target.getBoundingClientRect();
    const view = this.doc.documentElement;
    const offScreen = rect.bottom < INSET * 4 || (view.clientHeight > 0 && rect.top > view.clientHeight - INSET * 4);
    if (!this.target.isConnected || !isLargeEnough(rect) || offScreen) {
      this.hide();
      return;
    }

    // D-81: the button keeps its corner of the screen fixed and the menu grows away from it, so
    // near the bottom the menu opens upwards and near the left edge it opens to the right.
    const room = this.room(rect);
    const { width, height } = this.ui.main.getBoundingClientRect();
    const left = room.left + this.position.x * room.width;
    const top = room.top + this.position.y * room.height;
    const fromRight = this.position.x >= 0.5;
    const fromBottom = this.position.y > 0.5;
    const style = this.ui.host.style;
    style.setProperty('left', fromRight ? 'auto' : `${left}px`, 'important');
    style.setProperty('right', fromRight ? `${room.viewWidth - left - width}px` : 'auto', 'important');
    style.setProperty('top', fromBottom ? 'auto' : `${top}px`, 'important');
    style.setProperty('bottom', fromBottom ? `${room.viewHeight - top - height}px` : 'auto', 'important');
    this.ui.wrap.classList.toggle('left', !fromRight);
    this.ui.wrap.classList.toggle('up', fromBottom);
  }

  /**
   * D-81: where the button's top left corner may go: the part of the video on screen, less the
   * inset and the button itself. Without a layout (no viewport size) the video's edges stand in.
   */
  private room(rect: DOMRect): { left: number; top: number; width: number; height: number; viewWidth: number; viewHeight: number } {
    const view = this.doc.documentElement;
    const viewWidth = view.clientWidth > 0 ? view.clientWidth : rect.right;
    const viewHeight = view.clientHeight > 0 ? view.clientHeight : rect.bottom;
    const { width, height } = this.ui!.main.getBoundingClientRect();
    const left = Math.max(rect.left, 0) + INSET;
    const top = Math.max(rect.top, 0) + INSET;
    return {
      left,
      top,
      width: Math.max(0, Math.min(rect.right, viewWidth) - INSET - width - left),
      height: Math.max(0, Math.min(rect.bottom, viewHeight) - INSET - height - top),
      viewWidth,
      viewHeight,
    };
  }

  /** D-81: a press on the button may become a drag; only a release without moving is a click. */
  private onDragStart(event: PointerEvent): void {
    this.dragEnded = false;
    if (event.button !== 0 || !this.target || !this.ui) return;
    const room = this.room(this.target.getBoundingClientRect());
    this.drag = {
      pointerId: typeof event.pointerId === 'number' ? event.pointerId : undefined,
      startX: event.clientX,
      startY: event.clientY,
      left: room.left + this.position.x * room.width,
      top: room.top + this.position.y * room.height,
      moving: false,
    };
    if (this.drag.pointerId !== undefined) {
      try {
        this.ui.main.setPointerCapture(this.drag.pointerId); // keep the moves coming however fast the pointer goes
      } catch {
        // The pointer is already gone.
      }
    }
  }

  private onDragMove(event: PointerEvent): void {
    const drag = this.drag;
    if (!drag || !this.target || !this.ui) return;
    const dx = event.clientX - drag.startX;
    const dy = event.clientY - drag.startY;
    if (!drag.moving) {
      if (Math.hypot(dx, dy) < DRAG_THRESHOLD) return;
      drag.moving = true;
      this.ui.menu.hidden = true;
      this.menuPage = undefined;
      this.ui.main.classList.add('dragging');
      this.cancelHide();
    }

    const room = this.room(this.target.getBoundingClientRect());
    const fraction = (offset: number, span: number, fallback: number) => (span > 0 ? Math.min(1, Math.max(0, offset / span)) : fallback);
    this.position = {
      x: fraction(drag.left + dx - room.left, room.width, this.position.x),
      y: fraction(drag.top + dy - room.top, room.height, this.position.y),
    };
    this.reposition();
  }

  private onDragEnd(): void {
    const drag = this.drag;
    if (!drag) return;
    this.drag = undefined;
    if (drag.pointerId !== undefined && this.ui?.main.hasPointerCapture?.(drag.pointerId)) {
      this.ui.main.releasePointerCapture(drag.pointerId);
    }
    if (!drag.moving) return;
    this.ui?.main.classList.remove('dragging');
    this.dragEnded = true;
    this.onMoved?.(this.position);
  }

  /** In fullscreen only the fullscreen element's subtree is drawn, so the button moves into it. */
  private placeHost(): void {
    if (!this.ui) return;
    const full = this.doc.fullscreenElement;
    const parent = full && full.localName !== 'video' ? full : this.doc.documentElement;
    if (this.ui.host.parentNode !== parent) parent.append(this.ui.host);
  }

  private onFullscreenChange(): void {
    if (!this.ui || !this.target) return;
    if (this.doc.fullscreenElement === this.target) {
      this.hide(); // the bare <video> is fullscreen: nothing else can be drawn over it
      return;
    }
    this.placeHost();
    this.reposition();
  }

  private onMainClick(): void {
    if (this.dragEnded) {
      this.dragEnded = false; // the release of a drag
      return;
    }
    if (this.busy) return;
    if (this.candidates.length === 1 && !this.choosesQuality(this.candidates[0])) {
      void this.sendMedia(this.candidates[0]);
    } else if (this.ui?.menu.hidden) {
      if (this.candidates.length === 1) void this.openQualityMenu(this.candidates[0]);
      else this.openMenu();
    } else if (this.ui) {
      this.ui.menu.hidden = true;
      this.menuPage = undefined;
    }
  }

  /** D-72: a video page whose qualities OB2 can list right here. */
  private choosesQuality(media: MediaCandidate | undefined): boolean {
    return !!media?.page && !media.format && !!this.listFormats;
  }

  private openMenu(): void {
    this.menuPage = undefined;
    this.fillMenu(
      this.candidates.map((media) =>
        this.menuItem(mediaLabel(media, formatBytes), media.url, () => void (this.choosesQuality(media) ? this.openQualityMenu(media) : this.sendMedia(media))),
      ),
    );
  }

  /**
   * D-72: OB2 lists what the page offers (yt-dlp, a few seconds); each entry downloads that
   * quality, and the last one opens OB2's Download Video window for other audio tracks.
   */
  private async openQualityMenu(page: MediaCandidate): Promise<void> {
    const ui = this.ui!;
    const streams = this.streamsForPage();
    // D-79: an encrypted player with no manifest to try (Netflix fetches its own) can only fail.
    const encrypted = this.isEncrypted() && !youTubeVideoPage(page.url);
    if (encrypted && streams.length === 0) {
      this.menuPage = undefined;
      this.fillMenu([this.menuNote(PROTECTED_TEXT)]);
      return;
    }

    this.menuPage = page.url;
    this.fillMenu([this.menuNote('Getting the qualities from OB2…')]);
    ui.menu.setAttribute('aria-busy', 'true');
    const outcome = await this.formatsOf(page.url, streams);
    if (ui.menu.hidden || this.menuPage !== page.url) return; // closed, or another video, meanwhile

    const whole = { url: page.url, page: true } satisfies MediaCandidate;
    if (outcome.action === 'listed') {
      // D-79: what OB2 listed is what it downloads, the page or the stream its player loaded.
      // D-80: a stream goes with the headers the browser sent for it.
      const stream = streams.find((s) => s.url === outcome.url);
      const source: MediaCandidate = stream ? { url: stream.url, page: true, headers: stream.headers } : { url: page.url, page: true };
      const name = outcome.title.trim().slice(0, 200) || undefined;
      const items = outcome.options.map((option) => {
        const label = optionLabel(option, formatBytes);
        const choice: MediaCandidate = { ...source, format: option.format, container: option.container, name };
        if (option.size && option.size > 0) choice.size = option.size;
        return this.menuItem(label, label, () => void this.sendMedia(choice));
      });
      const more = this.menuItem('More choices in OB2… (audio track, folder)', source.url, () => void this.sendMedia(source));
      more.querySelector('button')!.classList.add('more');
      this.fillMenu([...items, ...this.subtitleItems(outcome.subtitles, source, name), more]);
      return;
    }

    if (encrypted && outcome.reason === 'media-failed') {
      this.fillMenu([this.menuNote(PROTECTED_TEXT)]);
      return;
    }

    const items = [this.menuNote(outcome.message || FORMATS_TEXT[outcome.reason])];
    if (outcome.reason === 'tools-missing') {
      items.push(this.menuItem('Set up video downloads in OB2…', page.url, () => void this.sendMedia(whole)));
    } else if (outcome.reason !== 'unreachable' && outcome.reason !== 'incognito') {
      items.push(this.menuItem('Open in OB2…', page.url, () => void this.sendMedia(whole)));
    }
    this.fillMenu(items);
  }

  /** D-79: the video plays through Encrypted Media Extensions, i.e. under DRM. */
  private isEncrypted(): boolean {
    return !!this.target?.mediaKeys;
  }

  /**
   * D-74: a "Subtitles" heading, then one row per track with a button per format; each button
   * downloads that track as a file of its own, named after the video. Nothing from an OB2 that
   * predates subtitles.
   */
  private subtitleItems(subtitles: MediaSubtitle[] | undefined, page: MediaCandidate, name: string | undefined): HTMLLIElement[] {
    if (!subtitles) return [];
    const heading = this.menuNote(subtitles.length > 0 ? 'Subtitles' : 'Subtitles: none on this video');
    heading.classList.add('heading');
    return [heading, ...subtitles.map((subtitle) => this.subtitleRow(subtitle, page, name))];
  }

  private subtitleRow(subtitle: MediaSubtitle, page: MediaCandidate, name: string | undefined): HTMLLIElement {
    const li = this.doc.createElement('li');
    li.setAttribute('role', 'none');
    li.className = 'sub';
    const label = this.doc.createElement('span');
    label.className = 'name';
    label.textContent = subtitleLabel(subtitle);
    label.title = label.textContent;
    li.append(label);
    for (const container of subtitle.containers) {
      const button = this.doc.createElement('button');
      button.type = 'button';
      button.className = 'fmt';
      button.setAttribute('role', 'menuitem');
      button.textContent = container.toUpperCase();
      button.title = `${subtitleLabel(subtitle)} subtitles as ${container.toUpperCase()}`;
      button.setAttribute('aria-label', button.title);
      const choice: MediaCandidate = { ...page, format: subtitle.format, container, name };
      button.addEventListener('click', () => void this.sendMedia(choice));
      li.append(button);
    }
    return li;
  }

  /**
   * The page's qualities, asked once and kept for a while; a failure is asked again next time.
   * D-79: a stream the player loaded since is a new question.
   */
  private formatsOf(pageUrl: string, streams: readonly StreamInfo[]): Promise<FormatsOutcome> {
    const now = Date.now();
    for (const [key, kept] of this.formats) {
      if (now - kept.at >= FORMATS_REUSE_MS) this.formats.delete(key);
    }
    const key = [pageUrl, ...streams.map((s) => s.url)].join('\n');
    const kept = this.formats.get(key);
    if (kept) return kept.outcome;

    const outcome = this.listFormats!(pageUrl, streams).catch((): FormatsOutcome => ({ action: 'failed', reason: 'failed' }));
    this.formats.set(key, { at: now, outcome });
    void outcome.then((o) => {
      if (o.action !== 'listed' && this.formats.get(key)?.outcome === outcome) this.formats.delete(key);
    });
    return outcome;
  }

  private fillMenu(items: HTMLLIElement[]): void {
    const ui = this.ui!;
    ui.menu.replaceChildren(...items);
    ui.menu.removeAttribute('aria-busy');
    ui.menu.hidden = false;
    (ui.menu.querySelector('button') as HTMLButtonElement | null)?.focus();
  }

  private menuItem(text: string, title: string, onClick: () => void): HTMLLIElement {
    const li = this.doc.createElement('li');
    li.setAttribute('role', 'none');
    const item = this.doc.createElement('button');
    item.type = 'button';
    item.className = 'item';
    item.setAttribute('role', 'menuitem');
    item.textContent = text;
    item.title = title;
    item.addEventListener('click', onClick);
    li.append(item);
    return li;
  }

  private menuNote(text: string): HTMLLIElement {
    const li = this.doc.createElement('li');
    li.setAttribute('role', 'none');
    li.className = 'note';
    li.textContent = text;
    return li;
  }

  private async sendMedia(media: MediaCandidate): Promise<void> {
    const ui = this.ui!;
    this.busy = true;
    this.menuPage = undefined;
    ui.menu.hidden = true;
    ui.main.disabled = true;
    this.setLabel('Sending to OB2…');
    let result: SendResult;
    try {
      result = await this.send(media);
    } catch {
      result = 'failed';
    }
    this.setLabel(RESULT_TEXT[result]);
    setTimeout(() => {
      this.busy = false;
      ui.main.disabled = false;
      this.hide();
    }, RESULT_SHOW_MS);
  }

  private menuOpen(): boolean {
    return !!this.ui && !this.ui.menu.hidden;
  }

  private setLabel(text: string): void {
    if (this.ui) this.ui.label.textContent = text;
  }

  private scheduleHide(ms: number): void {
    this.cancelHide();
    this.hideTimer = setTimeout(() => {
      if (!this.busy && !this.menuOpen()) this.hide();
    }, ms);
  }

  private cancelHide(): void {
    clearTimeout(this.hideTimer);
    this.hideTimer = undefined;
  }

  private build(): Ui {
    const doc = this.doc;
    const host = doc.createElement('ob2-media-button');
    for (const [name, value] of [['position', 'fixed'], ['z-index', '2147483647'], ['margin', '0'], ['padding', '0'], ['border', '0'], ['width', 'auto'], ['height', 'auto'], ['left', 'auto'], ['bottom', 'auto']]) {
      host.style.setProperty(name, value, 'important');
    }
    // The page's own handlers (players toggle play on click) must not see clicks on the button.
    for (const type of ['click', 'dblclick', 'pointerdown', 'pointerup', 'mousedown', 'mouseup', 'contextmenu']) {
      host.addEventListener(type, (e) => e.stopPropagation());
    }

    const root = host.attachShadow({ mode: this.shadowMode });
    if ('adoptedStyleSheets' in root && typeof CSSStyleSheet !== 'undefined' && 'replaceSync' in CSSStyleSheet.prototype) {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(CSS);
      root.adoptedStyleSheets = [sheet];
    } else {
      const style = doc.createElement('style');
      style.textContent = CSS;
      root.append(style);
    }

    const wrap = doc.createElement('div');
    wrap.className = 'wrap';
    const main = doc.createElement('button');
    main.type = 'button';
    main.className = 'main';
    main.title = 'Drag to move this button';
    main.append(downloadIcon(doc));
    const label = doc.createElement('span');
    main.append(label);
    main.addEventListener('click', () => this.onMainClick());
    main.addEventListener('pointerdown', (e) => this.onDragStart(e));
    main.addEventListener('pointermove', (e) => this.onDragMove(e));
    main.addEventListener('pointerup', () => this.onDragEnd());
    main.addEventListener('pointercancel', () => this.onDragEnd());
    const menu = doc.createElement('ul');
    menu.className = 'menu';
    menu.setAttribute('role', 'menu');
    menu.hidden = true;
    wrap.append(main, menu);
    root.append(wrap);
    return { host, wrap, main, label, menu };
  }
}

/** A single-page site changes the address per video; the part after `#` is not another page. */
function withoutHash(url: string): string {
  const hash = url.indexOf('#');
  return hash < 0 ? url : url.slice(0, hash);
}

function isLargeEnough(rect: DOMRect): boolean {
  return rect.width >= MIN_WIDTH && rect.height >= MIN_HEIGHT;
}

function setDisplay(host: HTMLElement, visible: boolean): void {
  host.style.setProperty('display', visible ? 'block' : 'none', 'important');
}

function mediaInPath(event: Event): HTMLMediaElement | undefined {
  for (const node of event.composedPath()) {
    if (node instanceof Element && (node.localName === 'video' || node.localName === 'audio')) return node as HTMLMediaElement;
  }
  return undefined;
}

function downloadIcon(doc: Document): SVGSVGElement {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = doc.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const path = doc.createElementNS(ns, 'path');
  path.setAttribute('d', 'M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 19.5h14');
  path.setAttribute('fill', 'none');
  path.setAttribute('stroke', 'currentColor');
  path.setAttribute('stroke-width', '2');
  path.setAttribute('stroke-linecap', 'round');
  path.setAttribute('stroke-linejoin', 'round');
  svg.append(path);
  return svg;
}
