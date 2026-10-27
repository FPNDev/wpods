/* Nearby popup entry (NearbyPopup component). Protocol: scratchpad ui-protocol.md (v2), section 4.
   Never focuses itself; the host focuses the window on Win+Shift+A and sends `focus`. */
import { applyCommon } from '../common';
import {
  createAnnouncer,
  createFrame,
  setAttr,
  setHidden,
  setText,
} from '../dom';
import { listen, post } from '../host';
import { t } from '../i18n';
import { Icon } from '../icons';
import type { HostToPopup, Offer } from '../protocol';
import { createOfferView } from './offer';
import { createOfferList } from './offerList';

type View = 'none' | 'offer' | 'connecting' | 'connected' | 'failed' | 'multi';

let ready = false;
let offers: Offer[] = [];
let view: View = 'none';
let dev: Offer | null = null;
let seen = new Set<string>();
let announceText = '';
let confirmFor: string | null = null;
let lastHeight = 0;

const dismiss = (): void => {
  if (view === 'connecting') {
    post({ type: 'popupCancel' });
  }
  const ids = view === 'multi' ? offers.map((o) => o.id) : dev ? [dev.id] : [];
  if (ids.length) {
    post({ type: 'popupDismiss', ids });
  }
};

const offerView = createOfferView({
  onConnect: () => {
    if (view === 'offer' && dev) {
      post({ type: 'popupConnect', id: dev.id });
    }
  },
  onSecondary: () => {
    if (view === 'connecting') {
      post({ type: 'popupCancel' });
    } else {
      dismiss();
    }
  },
});
const list = createOfferList({
  onPick: (id) => post({ type: 'popupConnect', id }),
  onNotNow: dismiss,
});

let card!: HTMLDivElement;
let done!: HTMLDivElement;
let doneName!: HTMLParagraphElement;
let failed!: HTMLDivElement;
let retry!: HTMLButtonElement;
let close!: HTMLButtonElement;
let announce!: HTMLDivElement;
const root = (
  <div class="wp-popup" role="dialog" hidden ref={card}>
    {offerView.el}
    {list.el}
    <div class="wp-popup__view" hidden ref={done}>
      <span class="wp-popup__done wp-popup__mark">
        <Icon name="check" />
      </span>
      <div role="status">
        <p class="wp-popup__name" l:text="popup.connected" />
        <p class="wp-popup__sub" ref={doneName} />
      </div>
      <div class="wp-popup__spacer" />
    </div>
    <div class="wp-popup__view" hidden ref={failed}>
      <span class="wp-popup__fail wp-popup__mark">
        <Icon name="warning" class="wp-icon--lg" />
      </span>
      <div role="alert">
        <p class="wp-popup__name" l:text="popup.failedTitle" />
        <p class="wp-popup__sub" l:text="popup.failedBody" />
      </div>
      <div class="wp-popup__actions">
        <button
          type="button"
          class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
          l:text="popup.tryAgain"
          ref={retry}
          onclick={() => {
            if (dev) {
              post({ type: 'popupConnect', id: dev.id });
            }
          }}
        />
        <button
          type="button"
          class="wp-btn wp-btn--subtle wp-btn--block"
          l:text="popup.notNow"
          onclick={dismiss}
        />
      </div>
    </div>
    <button
      type="button"
      class="wp-iconbtn wp-popup__close"
      l:aria-label="popup.close"
      ref={close}
      onclick={dismiss}
    >
      <Icon name="close" />
    </button>
    <div class="wp-sr" role="status" ref={announce} />
  </div>
);
document.body.append(root);
const announcer = createAnnouncer(announce);

const setOffers = (next: Offer[]): void => {
  offers = next;
  let active: Offer | null = null;
  const now = new Set<string>();
  for (const o of next) {
    if (!active && o.state !== 'offer') {
      active = o;
    }
    now.add(o.id);
    if (!seen.has(o.id)) {
      announceText = t('popup.nearbyA11y', { name: o.name });
    }
  }
  seen = now;
  dev = active ?? (next.length === 1 ? next[0] : null);
  view = active
    ? active.state
    : next.length === 1
      ? 'offer'
      : next.length > 1
        ? 'multi'
        : 'none';
  if (view !== 'connected') {
    confirmFor = null;
  }
};

const render = (): void => {
  if (!ready) {
    return;
  }
  setHidden(card, view === 'none');
  setHidden(offerView.el, view !== 'offer' && view !== 'connecting');
  setHidden(list.el, view !== 'multi');
  setHidden(done, view !== 'connected');
  setHidden(failed, view !== 'failed');
  if (announceText) {
    const text = announceText;
    announceText = '';
    announcer(text);
  }
  if (view === 'multi') {
    list.render(offers);
    setAttr(card, 'aria-label', t('popup.multiTitle'));
    return;
  }
  if (!dev) {
    return;
  }
  setAttr(card, 'aria-label', t('popup.nearbyA11y', { name: dev.name }));
  if (view === 'offer' || view === 'connecting') {
    offerView.render(dev, view === 'connecting');
  } else if (view === 'connected') {
    setText(doneName, dev.name);
    if (confirmFor !== dev.id) {
      confirmFor = dev.id;
      const id = dev.id;
      const ms =
        parseFloat(
          getComputedStyle(card).getPropertyValue('--dur-popup-confirm'),
        ) || 2500;
      setTimeout(() => post({ type: 'popupDismiss', ids: [id] }), ms);
    }
  }
};
const schedule = createFrame(render);

listen<HostToPopup>((msg) => {
  applyCommon(msg);
  switch (msg.type) {
    case 'init':
      setOffers(msg.offers);
      ready = true;
      break;
    case 'offers':
      setOffers(msg.offers);
      break;
    case 'focus':
      requestAnimationFrame(() => {
        const target =
          view === 'multi'
            ? list.first()
            : view === 'failed'
              ? retry
              : view === 'connected'
                ? close
                : offerView.connect;
        target?.focus();
      });
      break;
    default:
      break;
  }
  schedule();
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    dismiss();
  }
});

new ResizeObserver((entries) => {
  const height = Math.ceil(entries[0].borderBoxSize[0].blockSize);
  if (height !== lastHeight) {
    lastHeight = height;
    post({ type: 'popupSize', height });
  }
}).observe(card);

post({ type: 'ready', page: 'popup' });
