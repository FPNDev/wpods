/* Hotkey notification entry: a card with one line per changed setting. It never takes focus. It stays up while an
   item is pending (its spinner shows only if that takes a moment), then fades out and tells the host to hide. The
   host reveals the window once the page painted and only then marks it visible, so the entry animation waits. */
import { applyCommon } from '../common';
import { setAttr, setClass, setHidden, setText } from '../dom';
import { listen, post } from '../host';
import { Icon, Spinner } from '../icons';
import type { HostToOsd, OsdItem, OsdTone } from '../protocol';

type Line = {
  el: HTMLDivElement;
  badge: HTMLSpanElement;
  title: HTMLSpanElement;
  detail: HTMLSpanElement;
  icon: string;
  tone: OsdTone | null;
};

const HOLD_MS = 1800;
const HOLD_FAIL_MS = 3200;
// A pending item that never resolves still lets the card go.
const PENDING_MAX_MS = 15000;
const LEAVE_MS = 160;
const READY_MAX_MS = 250;

let card!: HTMLDivElement;
const root = (<div class="wp-osd" role="status" hidden ref={card} />) as HTMLDivElement;
document.body.append(root);

const lines: Line[] = [];
let seq = 0;
let holdTimer = 0;
let leaveTimer = 0;

const createLine = (): Line => {
  let badge!: HTMLSpanElement;
  let title!: HTMLSpanElement;
  let detail!: HTMLSpanElement;
  const el = (
    <div class="wp-osd__line">
      <span class="wp-badge wp-badge--lg" ref={badge} />
      <span class="wp-osd__text">
        <span class="wp-osd__title" ref={title} />
        <span class="wp-osd__detail" ref={detail} />
      </span>
    </div>
  ) as HTMLDivElement;

  return { el, badge, title, detail, icon: '', tone: null };
};

const renderLine = (line: Line, item: OsdItem): void => {
  const tone = item.tone;
  if (line.icon !== item.icon || (line.tone === 'pending') !== (tone === 'pending')) {
    line.badge.replaceChildren(Icon({ name: item.icon }));
    if (tone === 'pending') {
      line.badge.append(<Spinner class="wp-badge__spin" />);
    }
    line.icon = item.icon;
  }
  // A result replacing a pending state pops in.
  if (line.tone === 'pending' && tone !== 'pending') {
    line.badge.classList.remove('is-done');
    void line.badge.offsetWidth;
    line.badge.classList.add('is-done');
  }
  setAttr(line.badge, 'data-tone', tone);
  line.tone = tone;
  setText(line.title, item.title);
  setText(line.detail, item.detail);
};

// Measured at once, before the window has this size; the layout size leaves out the entry animation's scale.
const reportSize = (): void => {
  post({
    type: 'osdSize',
    width: card.offsetWidth,
    height: card.offsetHeight,
  });
};

const leave = (): void => {
  clearTimeout(holdTimer);
  setClass(card, 'is-leaving', true);
  const hid = seq;
  leaveTimer = setTimeout(() => {
    setHidden(card, true);
    setClass(card, 'is-leaving', false);
    for (const line of lines) {
      line.tone = null;
    }
    post({ type: 'osdHidden', value: hid });
  }, LEAVE_MS);
};

const show = (items: OsdItem[]): void => {
  clearTimeout(holdTimer);
  clearTimeout(leaveTimer);
  setClass(card, 'is-leaving', false);
  const entering = card.hidden;
  while (lines.length < items.length) {
    const line = createLine();
    lines.push(line);
    card.append(line.el);
  }
  let pending = false;
  let failed = false;
  for (const [i, line] of lines.entries()) {
    const item = items.at(i);
    setHidden(line.el, !item);
    if (item) {
      renderLine(line, item);
      pending ||= item.tone === 'pending';
      failed ||= item.tone === 'fail';
    }
  }
  if (entering) {
    setHidden(card, false);
    card.classList.remove('is-entering');
    void card.offsetWidth;
    card.classList.add('is-entering');
  }
  holdTimer = setTimeout(
    leave,
    pending ? PENDING_MAX_MS : failed ? HOLD_FAIL_MS : HOLD_MS,
  );
  reportSize();
};

// Two frames after the host sized the window, the new content has been painted.
const reportPainted = (): void => {
  let sent = false;
  const send = (): void => {
    if (!sent) {
      sent = true;
      post({ type: 'osdReady' });
    }
  };
  requestAnimationFrame(() => {
    requestAnimationFrame(send);
  });
  // Frames may not come for a window Windows considers hidden; the card shows anyway.
  setTimeout(send, READY_MAX_MS);
};

const onMessage = (msg: HostToOsd): void => {
  applyCommon(msg, onMessage);
  if (msg.type === 'osd') {
    seq = msg.seq;
    show(msg.items);
  } else if (msg.type === 'osdPlaced') {
    reportPainted();
  }
};
listen<HostToOsd>(onMessage);

post({ type: 'ready', page: 'osd' });
