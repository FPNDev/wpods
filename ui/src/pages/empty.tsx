import { PairSteps } from "../components/common";
import { bindAttr } from "../i18n";
import { Art, Icon } from "../icons";
import { post } from "../host";
import { LegalFoot } from "./legal";
import type { Ctx, Page } from "./page";

export const createEmpty = (ctx: Ctx): Page => {
  const legal = LegalFoot(ctx);
  const art = Art({ model: "empty", class: "wp-empty__art" });
  bindAttr(art, "aria-label", "art.empty");
  const el = (
    <div class="wp-page">
      <div class="wp-empty">
        {art}
        <h1 class="wp-empty__title" l:text="empty.title" />
        <p class="wp-empty__body" l:text="empty.body" />
        <div class="wp-empty__actions">
          <button
            type="button"
            class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
            onclick={() => post({ type: "openBluetoothSettings" })}
          >
            <Icon name="bluetooth" />
            <span l:text="empty.openBluetooth" />
          </button>
        </div>
        <PairSteps />
      </div>
      {legal.el}
    </div>
  ) as HTMLDivElement;

  return { el, sections: {}, returnFocus: () => legal.link };
};
