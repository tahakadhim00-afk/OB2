// FR-24: the "Download with OB2" button that floats over a video while the pointer is on it, and
// for a few seconds after a video starts playing. It offers the video's own file when
// the element plays an http(s) address, else the media files the background saw this frame load
// (players that feed the element a blob: URL). The button lives in a closed shadow root so page
// CSS and scripts can't restyle or read it, and it is built without innerHTML (Trusted Types).

import { formatBytes } from './format';
import { isDownloadableMediaUrl, isYouTubeHost, mediaLabel, youTubeVideoPage, type MediaCandidate } from './media';
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

const RESULT_TEXT: Record<SendResult, string> = {
  sent: 'Sent to OB2',
  browser: 'OB2 not reachable: the browser is downloading it',
  unreachable: 'OB2 not reachable. Is it set up? (OB2 popup)',
  incognito: 'Not available in Incognito',
  failed: 'Could not send to OB2',
};

const CSS = `
:host { all: initial; }
.wrap { font: 13px/1.3 "Segoe UI", system-ui, sans-serif; display: flex; flex-direction: column; align-items: flex-end; }
.main { display: inline-flex; align-items: center; gap: 6px; padding: 6px 10px; border: 0; border-radius: 4px;
  background: #1a73e8; color: #fff; font: inherit; cursor: pointer; box-shadow: 0 1px 4px rgba(0, 0, 0, .45); }
.main:hover { background: #1765cc; }
.main:focus-visible { outline: 2px solid #fff; outline-offset: -4px; }
.main:disabled { cursor: default; background: #3c4043; }
.main svg { width: 16px; height: 16px; flex: none; }
.menu { list-style: none; margin: 4px 0 0; padding: 4px 0; min-width: 220px; max-width: 360px; max-height: 240px; overflow: auto;
  background: #fff; color: #202124; border-radius: 4px; box-shadow: 0 2px 8px rgba(0, 0, 0, .35); }
.menu[hidden] { display: none; }
.item { display: block; width: 100%; box-sizing: border-box; padding: 6px 10px; border: 0; background: none; color: inherit;
  font: inherit; text-align: left; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.item:hover, .item:focus-visible { background: #e8f0fe; outline: none; }
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
  main: HTMLButtonElement;
  label: HTMLSpanElement;
  menu: HTMLUListElement;
}

export class MediaOverlay {
  enabled = true;
  private readonly detected: MediaCandidate[] = [];
  private ui?: Ui;
  private target?: HTMLVideoElement;
  private candidates: MediaCandidate[] = [];
  private pointer = { x: -1, y: -1 };
  private frame = 0;
  private hideTimer?: ReturnType<typeof setTimeout>;
  private busy = false;
  private contextMedia?: HTMLMediaElement;
  private pageScan?: { at: number; media: MediaCandidate[] };

  /** `shadowMode` is open only in tests, which need to reach the button. */
  constructor(
    private readonly doc: Document,
    private readonly send: (media: MediaCandidate) => Promise<SendResult>,
    private readonly shadowMode: ShadowRootMode = 'closed',
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
    ui.main.setAttribute('aria-haspopup', candidates.length > 1 ? 'menu' : 'false');
    setDisplay(ui.host, true);
    this.reposition();
    return true;
  }

  hide(): void {
    this.cancelHide();
    this.target = undefined;
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
    this.ui.host.style.setProperty('top', `${Math.max(rect.top, 0) + INSET}px`, 'important');
    this.ui.host.style.setProperty('right', `${Math.max(view.clientWidth - rect.right, 0) + INSET}px`, 'important');
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
    if (this.busy) return;
    if (this.candidates.length === 1) {
      void this.sendMedia(this.candidates[0]);
    } else if (this.ui?.menu.hidden) {
      this.openMenu();
    } else if (this.ui) {
      this.ui.menu.hidden = true;
    }
  }

  private openMenu(): void {
    const ui = this.ui!;
    ui.menu.replaceChildren(
      ...this.candidates.map((media) => {
        const li = this.doc.createElement('li');
        li.setAttribute('role', 'none');
        const item = this.doc.createElement('button');
        item.type = 'button';
        item.className = 'item';
        item.setAttribute('role', 'menuitem');
        item.textContent = mediaLabel(media, formatBytes);
        item.title = media.url;
        item.addEventListener('click', () => void this.sendMedia(media));
        li.append(item);
        return li;
      }),
    );
    ui.menu.hidden = false;
    (ui.menu.querySelector('button') as HTMLButtonElement | null)?.focus();
  }

  private async sendMedia(media: MediaCandidate): Promise<void> {
    const ui = this.ui!;
    this.busy = true;
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
    main.append(downloadIcon(doc));
    const label = doc.createElement('span');
    main.append(label);
    main.addEventListener('click', () => this.onMainClick());
    const menu = doc.createElement('ul');
    menu.className = 'menu';
    menu.setAttribute('role', 'menu');
    menu.hidden = true;
    wrap.append(main, menu);
    root.append(wrap);
    return { host, main, label, menu };
  }
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
