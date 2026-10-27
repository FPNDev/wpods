/* Overlay scrollbar for a scroller whose own bar is hidden (scrollbar-width: none): a thumb only, no track, drawn over
   the content so it takes no width. Shows while scrolling or hovered, fades out when idle; drag it to scroll. Goes in
   the scroller's positioned parent. */
import { createFrame, setClass, setHidden } from '../dom';

const MIN_THUMB = 24;
const INSET = 2;
const IDLE_MS = 1000;

export const createOverlayScroll = (scroller: HTMLElement): HTMLDivElement => {
  let travel = 0;
  let dragging = false;
  let dragY = 0;
  let dragTop = 0;
  let timer = 0;
  const update = (): void => {
    const view = scroller.clientHeight;
    const total = scroller.scrollHeight;
    setHidden(el, total <= view);
    if (total <= view) {
      return;
    }
    const size = Math.max(MIN_THUMB, (view * view) / total);
    travel = view - size - INSET * 2;
    const top =
      scroller.offsetTop +
      INSET +
      (scroller.scrollTop / (total - view)) * travel;
    el.style.height = `${size}px`;
    el.style.transform = `translateY(${top}px)`;
  };
  const show = (): void => {
    setClass(el, 'wp-scroll--on', true);
    clearTimeout(timer);
    timer = setTimeout(() => {
      if (!dragging) {
        setClass(el, 'wp-scroll--on', false);
      }
    }, IDLE_MS);
  };
  const endDrag = (): void => {
    dragging = false;
    setClass(el, 'wp-scroll--drag', false);
    show();
  };
  const el = (
    <div
      class="wp-scroll"
      aria-hidden="true"
      hidden
      onpointerdown={(e) => {
        if (e.button !== 0) {
          return;
        }
        e.preventDefault();
        el.setPointerCapture(e.pointerId);
        dragging = true;
        dragY = e.clientY;
        dragTop = scroller.scrollTop;
        setClass(el, 'wp-scroll--drag', true);
        show();
      }}
      onpointermove={(e) => {
        if (dragging && travel > 0) {
          scroller.scrollTop =
            dragTop +
            ((e.clientY - dragY) *
              (scroller.scrollHeight - scroller.clientHeight)) /
              travel;
        }
      }}
      onpointerup={endDrag}
      onpointercancel={endDrag}
    />
  ) as HTMLDivElement;

  const frame = createFrame(update);
  scroller.addEventListener(
    'scroll',
    () => {
      frame();
      show();
    },
    { passive: true },
  );
  const resized = new ResizeObserver(update);
  resized.observe(scroller);
  /* Pages are swapped in as children: watch each one so content growth updates the thumb too. */
  new MutationObserver((records) => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (node instanceof Element) {
          resized.observe(node);
        }
      }
      for (const node of record.removedNodes) {
        if (node instanceof Element) {
          resized.unobserve(node);
        }
      }
    }
  }).observe(scroller, { childList: true });
  
  return el;
};
