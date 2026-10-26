import { setText } from "../dom";
import { t } from "../i18n";
import { Glyph, Icon } from "../icons";
import type { Batteries, DeviceId, Offer } from "../protocol";

type OfferList = {
  el: HTMLDivElement;
  render: (offers: Offer[]) => void;
  first: () => HTMLButtonElement | null;
};

type PickButton = {
  btn: HTMLButtonElement;
  name: HTMLSpanElement;
  meta: HTMLSpanElement;
};

const batteryMeta = (b: Batteries): string => {
  if (b.headphone) {
    return t("battery.pct", { pct: b.headphone.pct });
  }
  let out = "";
  for (const [part, key] of [
    ["left", "popup.metaLeft"],
    ["right", "popup.metaRight"],
    ["case", "popup.metaCase"],
  ] as const) {
    const bat = b[part];
    if (bat) {
      out += (out ? " \u00B7 " : "") + t(key, { pct: bat.pct });
    }
  }
  return out;
};

export const createOfferList = (o: {
  onPick: (id: DeviceId) => void;
  onNotNow: () => void;
}): OfferList => {
  let sub!: HTMLParagraphElement;
  let picks!: HTMLDivElement;
  let first: HTMLButtonElement | null = null;
  const byId = new Map<DeviceId, PickButton>();
  const el = (
    <div class="wp-popup__view" hidden>
      <div class="wp-popup__head">
        <p class="wp-popup__name" l:text="popup.multiTitle" />
        <p class="wp-popup__sub" ref={sub} />
      </div>
      <div class="wp-popup__list" ref={picks} />
      <div class="wp-popup__actions">
        <button
          type="button"
          class="wp-btn wp-btn--subtle wp-btn--block"
          l:text="popup.notNow"
          onclick={o.onNotNow}
        />
      </div>
    </div>
  ) as HTMLDivElement;

  const createPick = (offer: Offer): PickButton => {
    let name!: HTMLSpanElement;
    let meta!: HTMLSpanElement;
    const btn = (
      <button type="button" class="wp-pick" onclick={() => o.onPick(offer.id)}>
        <span class="wp-pick__art">
          <Glyph name={offer.art === "max" ? "max" : "pair"} />
        </span>
        <span class="wp-pick__text">
          <span class="wp-pick__name" ref={name} />
          <span class="wp-pick__meta" ref={meta} />
        </span>
        <Icon name="chevron-right" class="wp-pick__end" />
      </button>
    ) as HTMLButtonElement;
    return { btn, name, meta };
  };

  return {
    el,
    render: (offers) => {
      const n = offers.length;
      setText(
        sub,
        t(
          n === 2
            ? "popup.multiSubTwo"
            : n === 3
              ? "popup.multiSubThree"
              : "popup.multiSubOther",
          { count: n },
        ),
      );
      /* Keyed by offer id: only buttons whose ids appeared or disappeared are added or removed, so a still-present
         button (and its focus) survives; a moved focused button takes focus back. A removed focused button hands
         focus to the pick now at its position, or the last pick if it was the last. */
      const active = document.activeElement;
      const ids = new Set<DeviceId>();
      for (const offer of offers) {
        ids.add(offer.id);
      }
      let lostAt = -1;
      for (const [id, pick] of byId) {
        if (!ids.has(id)) {
          if (pick.btn === active) {
            lostAt = Array.prototype.indexOf.call(picks.children, active);
          }
          pick.btn.remove();
          byId.delete(id);
        }
      }
      const focusAt = Math.min(lostAt, n - 1);
      first = null;
      let at = picks.firstChild;
      for (const [i, offer] of offers.entries()) {
        let pick = byId.get(offer.id);
        if (!pick) {
          pick = createPick(offer);
          byId.set(offer.id, pick);
        }
        setText(pick.name, offer.name);
        setText(pick.meta, batteryMeta(offer.batteries));
        if (at !== pick.btn) {
          picks.insertBefore(pick.btn, at);
          if (pick.btn === active) {
            pick.btn.focus();
          }
        }
        if (i === focusAt) {
          pick.btn.focus();
        }
        at = pick.btn.nextSibling;
        first ??= pick.btn;
      }
    },
    first: () => first,
  };
};
