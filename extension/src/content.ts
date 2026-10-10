// §8.4: holding Shift while clicking a link lets Chrome download it instead of OB2.
// Reports only the clicked link's address, only when Shift is held; reads nothing else.

export function shiftClickedLink(event: MouseEvent): string | undefined {
  if (!event.shiftKey) return undefined;
  // composedPath reaches links inside shadow roots; <a> covers both HTML and SVG links.
  for (const node of event.composedPath()) {
    if (node instanceof Element && node.localName === 'a') {
      const href = node.getAttribute('href') ?? node.getAttributeNS('http://www.w3.org/1999/xlink', 'href');
      if (!href) return undefined;
      try {
        return new URL(href, node.baseURI).href;
      } catch {
        return undefined;
      }
    }
  }
  return undefined;
}

function onClick(event: MouseEvent): void {
  const url = shiftClickedLink(event);
  if (url) {
    chrome.runtime.sendMessage({ type: 'ob2-bypass', url }).catch(() => undefined);
  }
}

document.addEventListener('click', onClick, true);
document.addEventListener('auxclick', onClick, true);
