/* Choice list when several of the user's AirPods are nearby: one pick per offer, Not now dismisses them all. */
import { setText } from '../dom';
import { labelsVersion, t } from '../i18n';
import { Glyph, Icon } from '../icons';
import type { Batteries, DeviceId, Offer } from '../protocol';

type OfferList = {
  el: HTMLDivElement;
  render: (offers: Offer[]) => void;
  first: () => HTMLButtonElement | null;
};

const batteryMeta = (b: Batteries): string => {
  if (b.headphone) {
    return t('battery.pct', { pct: b.headphone.pct });
  }
  let out = '';
  for (const [part, key] of [['left', 'popup.metaLeft'], ['right', 'popup.metaRight'], ['case', 'popup.metaCase']] as const) {
    const bat = b[part];
    if (bat) {
      out += (out ? ' \u00B7 ' : '') + t(key, { pct: bat.pct });
    }
  }
  return out;
};

export const createOfferList = (o: { onPick: (id: DeviceId) => void; onNotNow: () => void }): OfferList => {
  let sub!: HTMLParagraphElement;
  let picks!: HTMLDivElement;
  let first: HTMLButtonElement | null = null;
  let key = '';
  const el = (
    <div class="wp-popup__view" hidden>
      <div class="wp-popup__head"><p class="wp-popup__name" l:text="popup.multiTitle" /><p class="wp-popup__sub" ref={sub} /></div>
      <div class="wp-popup__list" ref={picks} />
      <div class="wp-popup__actions"><button type="button" class="wp-btn wp-btn--subtle wp-btn--block" l:text="popup.notNow" onclick={o.onNotNow} /></div>
    </div>
  ) as HTMLDivElement;

  return {
    el,
    render: (offers) => {
      const n = offers.length;
      setText(sub, t(n === 2 ? 'popup.multiSubTwo' : n === 3 ? 'popup.multiSubThree' : 'popup.multiSubOther', { count: n }));
      let next = String(labelsVersion);
      for (const offer of offers) {
        next += `\n${offer.id}|${offer.name}|${batteryMeta(offer.batteries)}`;
      }
      if (next === key) {
        return;
      }
      key = next;
      first = null;
      picks.replaceChildren();
      for (const offer of offers) {
        const pick = (
          <button type="button" class="wp-pick" onclick={() => o.onPick(offer.id)}>
            <span class="wp-pick__art"><Glyph name={offer.art === 'max' ? 'max' : 'pair'} /></span>
            <span class="wp-pick__text"><span class="wp-pick__name">{offer.name}</span><span class="wp-pick__meta">{batteryMeta(offer.batteries)}</span></span>
            <Icon name="chevron-right" class="wp-pick__end" />
          </button>
        ) as HTMLButtonElement;
        first ??= pick;
        picks.append(pick);
      }
    },
    first: () => first,
  };
};
