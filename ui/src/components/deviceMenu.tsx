import { setAttr, setHidden, setText } from "../dom";
import { t } from "../i18n";
import { Glyph, Icon } from "../icons";
import type { Device, DeviceId } from "../protocol";

type DeviceMenu = {
  el: HTMLDivElement;
  button: HTMLButtonElement;
  render: (devices: Device[], selected: Device) => void;
};

type Item = {
  btn: HTMLButtonElement;
  title: HTMLSpanElement;
  meta: HTMLSpanElement;
  check: SVGSVGElement;
  index: number;
};

export const createDeviceMenu = (o: {
  onPick: (id: DeviceId) => void;
  onPair: () => void;
}): DeviceMenu => {
  let button!: HTMLButtonElement;
  let buttonName!: HTMLSpanElement;
  let staticBox!: HTMLSpanElement;
  let staticName!: HTMLSpanElement;
  let label!: HTMLDivElement;
  let menu!: HTMLDivElement;
  let pair!: HTMLButtonElement;
  let open = false;
  let items: HTMLButtonElement[] = [];
  const byId = new Map<DeviceId, Item>();
  let focusAt = -1;
  let checkedAt = -1;
  let devices: Device[] = [];
  let selected: DeviceId | null = null;

  const focusItem = (i: number): void => {
    const n = items.length + 1;
    const k = ((i % n) + n) % n;
    (k === items.length ? pair : items[k]).focus();
  };
  const createItem = (d: Device): Item => {
    let title!: HTMLSpanElement;
    let meta!: HTMLSpanElement;
    const check = (
      <Icon name="check" class="wp-menu__check" />
    ) as SVGSVGElement;
    const btn = (
      <button
        type="button"
        class="wp-menu__item"
        role="menuitemradio"
        tabindex="-1"
        onfocus={() => {
          focusAt = item.index;
        }}
        onclick={() => {
          close(true);
          o.onPick(d.id);
        }}
      >
        <span class="wp-menu__glyph">
          <Glyph name={d.art === "max" ? "max" : "pair"} />
        </span>
        <span class="wp-menu__text">
          <span class="wp-menu__title" ref={title} />
          <span class="wp-menu__meta" ref={meta} />
        </span>
        {check}
      </button>
    ) as HTMLButtonElement;
    const item: Item = { btn, title, meta, check, index: 0 };
    return item;
  };
  /* Keyed by device id: only items whose ids appeared or disappeared are added or removed, so a still-present
     item (and its focus) survives. A moved or removed focused item hands focus back while the menu is open. */
  const syncItems = (): void => {
    const active = document.activeElement;
    const ids = new Set<DeviceId>();
    for (const d of devices) {
      ids.add(d.id);
    }
    let lost = false;
    for (const [id, item] of byId) {
      if (!ids.has(id)) {
        lost ||= item.btn === active;
        item.btn.remove();
        byId.delete(id);
      }
    }
    items = [];
    checkedAt = -1;
    let prev: Element = label;
    for (const [i, d] of devices.entries()) {
      let item = byId.get(d.id);
      if (!item) {
        item = createItem(d);
        byId.set(d.id, item);
      }
      const checked = d.id === selected;
      if (checked) {
        checkedAt = i;
      }
      item.index = i;
      setText(item.title, d.name);
      setText(item.meta, t(`status.${d.conn}`));
      setAttr(item.btn, "aria-checked", String(checked));
      setHidden(item.check, !checked);
      if (prev.nextSibling !== item.btn) {
        menu.insertBefore(item.btn, prev.nextSibling);
      }
      prev = item.btn;
      items.push(item.btn);
      if (item.btn === active) {
        focusAt = i;
        if (open && document.activeElement !== active) {
          item.btn.focus();
        }
      }
    }
    if (!open) {
      return;
    }
    if (lost) {
      focusItem(Math.min(focusAt, items.length));
    } else if (active === pair) {
      focusAt = items.length;
    }
  };
  const close = (refocus: boolean): void => {
    if (!open) {
      return;
    }
    open = false;
    setHidden(menu, true);
    button.setAttribute("aria-expanded", "false");
    if (refocus) {
      button.focus();
    }
  };
  const openMenu = (): void => {
    open = true;
    syncItems();
    setHidden(menu, false);
    button.setAttribute("aria-expanded", "true");
    focusItem(checkedAt < 0 ? items.length : checkedAt);
  };

  const el = (
    <div class="wp-hero__switch">
      <button
        type="button"
        class="wp-devswitch"
        aria-haspopup="menu"
        aria-expanded="false"
        ref={button}
        onclick={() => {
          if (open) {
            close(true);
          } else {
            openMenu();
          }
        }}
        onkeydown={(e) => {
          if (e.key === "ArrowDown" && !open) {
            e.preventDefault();
            openMenu();
          } else if (e.key === "Escape" && open) {
            e.stopPropagation();
            close(true);
          }
        }}
      >
        <span class="wp-devswitch__name" ref={buttonName} />
        <Icon name="chevron-down" />
      </button>
      <span class="wp-devswitch wp-devswitch--static" ref={staticBox}>
        <span class="wp-devswitch__name" ref={staticName} />
      </span>
      <div
        class="wp-menu"
        role="menu"
        l:aria-label="home.yourAirPods"
        hidden
        ref={menu}
        onkeydown={(e) => {
          let next: number | null = null;
          if (e.key === "ArrowDown") {
            next = focusAt + 1;
          } else if (e.key === "ArrowUp") {
            next = focusAt - 1;
          } else if (e.key === "Home") {
            next = 0;
          } else if (e.key === "End") {
            next = items.length;
          } else if (e.key === "Tab") {
            close(false);
          } else if (e.key === "Escape") {
            e.preventDefault();
            e.stopPropagation();
            close(true);
          }
          if (next !== null) {
            e.preventDefault();
            focusItem(next);
          }
        }}
      >
        <div class="wp-menu__label" l:text="home.yourAirPods" ref={label} />
        <div class="wp-menu__sep" role="separator" />
        <button
          type="button"
          class="wp-menu__item wp-menu__item--compact"
          role="menuitem"
          tabindex="-1"
          ref={pair}
          onfocus={() => {
            focusAt = items.length;
          }}
          onclick={() => {
            close(false);
            o.onPair();
          }}
        >
          <Icon name="plus" class="wp-menu__glyph" />
          <span class="wp-menu__title" l:text="menu.pairNew" />
          <Icon name="external" class="wp-menu__ext" />
        </button>
      </div>
    </div>
  ) as HTMLDivElement;

  document.addEventListener("pointerdown", (e) => {
    if (open && !el.contains(e.target as Node)) {
      close(false);
    }
  });

  return {
    el,
    button,
    render: (list, d) => {
      devices = list;
      selected = d.id;
      const multi = list.length > 1;
      setHidden(button, !multi);
      setHidden(staticBox, multi);
      setText(multi ? buttonName : staticName, d.name);
      if (!multi) {
        close(false);
      } else if (open) {
        syncItems();
      }
    },
  };
};
