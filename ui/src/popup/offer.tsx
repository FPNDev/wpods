/* Single offer view: art, name, "Connected to another device", batteries, Connect / Not now (Connecting... / Cancel). */
import { createBatteries } from '../components/batteryRing';
import { setAttr, setClass, setText } from '../dom';
import { t } from '../i18n';
import { Art, Spinner } from '../icons';
import type { Offer } from '../protocol';

type OfferView = {
  el: HTMLDivElement;
  connect: HTMLButtonElement;
  render: (offer: Offer, busy: boolean) => void;
};

export const createOfferView = (o: { onConnect: () => void; onSecondary: () => void }): OfferView => {
  let name!: HTMLParagraphElement;
  let connect!: HTMLButtonElement;
  let connectLabel!: HTMLSpanElement;
  let secondary!: HTMLButtonElement;
  let art = Art({ model: 'pro', class: 'wp-popup__art' });
  let artModel = 'pro';
  const batteries = createBatteries(true);
  const el = (
    <div class="wp-popup__view">
      {art}
      <div class="wp-popup__id"><p class="wp-popup__name" ref={name} /><p class="wp-popup__sub" l:text="status.elsewhere" /></div>
      {batteries.el}
      <div class="wp-popup__actions">
        <button type="button" class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block" ref={connect} onclick={o.onConnect}>
          <Spinner class="wp-spinner--sm" />
          <span ref={connectLabel} />
        </button>
        <button type="button" class="wp-btn wp-btn--subtle wp-btn--block" ref={secondary} onclick={o.onSecondary} />
      </div>
    </div>
  ) as HTMLDivElement;

  return {
    el,
    connect,
    render: (offer, busy) => {
      if (offer.art !== artModel) {
        artModel = offer.art;
        const next = Art({ model: offer.art, class: 'wp-popup__art' });
        art.replaceWith(next);
        art = next;
      }
      setAttr(art, 'aria-label', t(`art.${offer.art}`));
      setText(name, offer.name);
      batteries.update(offer.batteries, { left: '', right: '' }, offer.art === 'pro' ? 'case-pro' : 'case');
      setClass(connect, 'is-loading', busy);
      setAttr(connect, 'aria-disabled', busy ? 'true' : null);
      setText(connectLabel, t(busy ? 'popup.connecting' : 'popup.connect'));
      setText(secondary, t(busy ? 'popup.cancel' : 'popup.notNow'));
    },
  };
};
