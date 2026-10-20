/* WPods nearby popup (NearbyPopup component). Protocol: scratchpad ui-protocol.md.
   Never focuses itself; the host focuses it on Win+Shift+A and sends `focus`. */
'use strict';

const refs = collectRefs(document, {});
refs.art = refs.device.querySelector('.wp-popup__art');
const rings = { left: createRing('left', true), right: createRing('right', true), case: createRing('case', true), max: createRing('max', true) };
for (const r of Object.values(rings)) {
  refs.batteries.append(r.el);
}

let ready = false;
let offers = [];
let view = 'none';
let dev = null;
let artKey = '';
let picksKey = '';
let confirmMs = 2500;
let confirmFor = null;
let lastHeight = 0;
let announce = '';
let seen = new Set();

const batteryMeta = (b) => {
  if (!b) {
    return '';
  }
  if (b.headphone) {
    return t('battery.pct', { pct: b.headphone.pct });
  }
  let out = '';
  for (const [part, key] of [['left', 'popup.metaLeft'], ['right', 'popup.metaRight'], ['case', 'popup.metaCase']]) {
    if (b[part]) {
      out += (out ? ' · ' : '') + t(key, { pct: b[part].pct });
    }
  }
  return out;
};

const renderPicks = () => {
  let key = '';
  for (const o of offers) {
    key += `${o.id}|${o.name}|${batteryMeta(o.batteries)}\n`;
  }
  if (key === picksKey) {
    return;
  }
  picksKey = key;
  refs.picks.replaceChildren();
  for (const o of offers) {
    const el = html(`<button type="button" class="wp-pick"><span class="wp-pick__art">${WPods.glyph(o.art === 'max' ? 'max' : 'pair')}</span>` +
      `<span class="wp-pick__text"><span class="wp-pick__name"></span><span class="wp-pick__meta"></span></span>${WPods.icon('chevron-right', 'wp-pick__end')}</button>`);
    el.dataset.id = o.id;
    el.querySelector('.wp-pick__name').textContent = o.name;
    el.querySelector('.wp-pick__meta').textContent = batteryMeta(o.batteries);
    refs.picks.append(el);
  }
};

/* Runs once per offers message: the offer being acted on (connecting / connected / failed) wins, else one offer or the list. */
const setOffers = (list) => {
  offers = list;
  const active = list.find((o) => o.state !== 'offer');
  dev = active ?? (list.length === 1 ? list[0] : null);
  view = active ? active.state : list.length === 1 ? 'offer' : list.length > 1 ? 'multi' : 'none';
  const now = new Set();
  for (const o of list) {
    now.add(o.id);
    if (!seen.has(o.id)) {
      announce = t('popup.nearbyA11y', { name: o.name });
    }
  }
  seen = now;
};

const render = () => {
  if (!ready) {
    return;
  }
  setHidden(refs.card, view === 'none');
  setHidden(refs.device, view !== 'offer' && view !== 'connecting');
  setHidden(refs.multi, view !== 'multi');
  setHidden(refs.done, view !== 'connected');
  setHidden(refs.failed, view !== 'failed');
  if (announce) {
    setText(refs.announce, announce);
    announce = '';
  }

  if (view === 'multi') {
    renderPicks();
    const n = offers.length;
    setText(refs.multiSub, t(n === 2 ? 'popup.multiSubTwo' : n === 3 ? 'popup.multiSubThree' : 'popup.multiSubOther', { count: n }));
    setAttr(refs.card, 'aria-label', t('popup.multiTitle'));
  } else if (dev) {
    setAttr(refs.card, 'aria-label', t('popup.nearbyA11y', { name: dev.name }));
  }

  if (view === 'offer' || view === 'connecting') {
    if (dev.art !== artKey) {
      artKey = dev.art;
      const next = art(dev.art);
      next.classList.add('wp-popup__art');
      refs.art.replaceWith(next);
      refs.art = next;
    }
    setText(refs.name, dev.name);
    const b = dev.batteries;
    const caseGlyph = dev.art === 'pro' ? 'case-pro' : 'case';
    updateRing(rings.left, b?.left ?? null, '', caseGlyph);
    updateRing(rings.right, b?.right ?? null, '', caseGlyph);
    updateRing(rings.case, b?.case ?? null, '', caseGlyph);
    updateRing(rings.max, b?.headphone ?? null, '', caseGlyph);
    const busy = view === 'connecting';
    setClass(refs.connect, 'is-loading', busy);
    setAttr(refs.connect, 'aria-disabled', busy ? 'true' : null);
    setText(refs.connectLabel, t(busy ? 'popup.connecting' : 'popup.connect'));
    setText(refs.secondary, t(busy ? 'popup.cancel' : 'popup.notNow'));
  } else if (view === 'connected') {
    setText(refs.doneName, dev.name);
    if (confirmFor !== dev.id) {
      confirmFor = dev.id;
      const id = dev.id;
      setTimeout(() => post({ type: 'popupDismiss', ids: [id] }), confirmMs);
    }
  }
};

const apply = (msg) => {
  applyCommon(msg);
  switch (msg.type) {
    case 'init':
      applyLabels(document);
      confirmMs = parseFloat(getComputedStyle(root).getPropertyValue('--dur-popup-confirm')) || confirmMs;
      setOffers(msg.offers);
      ready = true;
      break;
    case 'offers':
      setOffers(msg.offers);
      break;
    case 'focus':
      requestAnimationFrame(() => {
        const target = view === 'multi' ? refs.picks.firstElementChild : view === 'failed' ? refs.retry : view === 'connected' ? refs.close : refs.connect;
        target?.focus();
      });
      break;
    default:
      break;
  }
};
listen(apply, render);

/* Actions -> host */
const dismiss = () => {
  if (view === 'connecting') {
    post({ type: 'popupCancel' });
  }
  const ids = [];
  for (const o of offers) {
    if (view === 'multi' || o === dev) {
      ids.push(o.id);
    }
  }
  if (ids.length) {
    post({ type: 'popupDismiss', ids });
  }
};
refs.connect.addEventListener('click', () => {
  if (view === 'offer') {
    post({ type: 'popupConnect', id: dev.id });
  }
});
refs.secondary.addEventListener('click', () => {
  if (view === 'connecting') {
    post({ type: 'popupCancel' });
  } else {
    dismiss();
  }
});
refs.retry.addEventListener('click', () => post({ type: 'popupConnect', id: dev.id }));
refs.failedNotNow.addEventListener('click', dismiss);
refs.multiNotNow.addEventListener('click', dismiss);
refs.close.addEventListener('click', dismiss);
refs.picks.addEventListener('click', (e) => {
  const pick = e.target.closest('.wp-pick');
  if (pick) {
    post({ type: 'popupConnect', id: pick.dataset.id });
  }
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    dismiss();
  }
});

/* The host sizes the window to the card (DIPs). */
new ResizeObserver((entries) => {
  const height = Math.ceil(entries[0].borderBoxSize[0].blockSize);
  if (height !== lastHeight) {
    lastHeight = height;
    post({ type: 'popupSize', height });
  }
}).observe(refs.card);

post({ type: 'ready', page: 'popup' });
