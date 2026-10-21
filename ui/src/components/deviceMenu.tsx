/* Device switcher in the hero: the name as a menu button (several pairs) or plain text (one pair), and the menu
   (role="menu", menuitemradio per pair, then "Pair new AirPods..."). Items are rebuilt while it is open. */
import { setHidden, setText } from '../dom';
import { t } from '../i18n';
import { Glyph, Icon } from '../icons';
import type { Device, DeviceId } from '../protocol';

type DeviceMenu = {
  el: HTMLDivElement;
  button: HTMLButtonElement;
  /* Updates the shown name; rebuilds the items when open. */
  render: (devices: Device[], selected: Device) => void;
};

export const createDeviceMenu = (o: { onPick: (id: DeviceId) => void; onPair: () => void }): DeviceMenu => {
  let button!: HTMLButtonElement;
  let buttonName!: HTMLSpanElement;
  let staticBox!: HTMLSpanElement;
  let staticName!: HTMLSpanElement;
  let menu!: HTMLDivElement;
  let sep!: HTMLDivElement;
  let pair!: HTMLButtonElement;
  let open = false;
  let items: HTMLButtonElement[] = [];
  let focusAt = -1;
  let checkedAt = -1;
  let devices: Device[] = [];
  let selected: DeviceId | null = null;

  const focusItem = (i: number): void => {
    const n = items.length + 1;
    const k = ((i % n) + n) % n;
    (k === items.length ? pair : items[k]).focus();
  };
  const buildItems = (): void => {
    for (const el of items) {
      el.remove();
    }
    items = [];
    checkedAt = -1;
    for (const d of devices) {
      const checked = d.id === selected;
      const index = items.length;
      if (checked) {
        checkedAt = index;
      }
      const check = <Icon name="check" class="wp-menu__check" />;
      const item = (
        <button type="button" class="wp-menu__item" role="menuitemradio" tabindex="-1" aria-checked={String(checked)}
          onfocus={() => {
            focusAt = index;
          }}
          onclick={() => {
            close(true);
            o.onPick(d.id);
          }}>
          <span class="wp-menu__glyph"><Glyph name={d.art === 'max' ? 'max' : 'pair'} /></span>
          <span class="wp-menu__text"><span class="wp-menu__title">{d.name}</span><span class="wp-menu__meta">{t(`status.${d.conn}`)}</span></span>
          {check}
        </button>
      ) as HTMLButtonElement;
      check.toggleAttribute('hidden', !checked);
      items.push(item);
      menu.insertBefore(item, sep);
    }
  };
  const close = (refocus: boolean): void => {
    if (!open) {
      return;
    }
    open = false;
    setHidden(menu, true);
    button.setAttribute('aria-expanded', 'false');
    if (refocus) {
      button.focus();
    }
  };
  const openMenu = (): void => {
    open = true;
    buildItems();
    setHidden(menu, false);
    button.setAttribute('aria-expanded', 'true');
    focusItem(checkedAt < 0 ? items.length : checkedAt);
  };

  const el = (
    <div class="wp-hero__switch">
      <button type="button" class="wp-devswitch" aria-haspopup="menu" aria-expanded="false" ref={button}
        onclick={() => {
          if (open) {
            close(true);
          } else {
            openMenu();
          }
        }}
        onkeydown={(e) => {
          if (e.key === 'ArrowDown' && !open) {
            e.preventDefault();
            openMenu();
          } else if (e.key === 'Escape' && open) {
            e.stopPropagation();
            close(true);
          }
        }}>
        <span class="wp-devswitch__name" ref={buttonName} />
        <Icon name="chevron-down" />
      </button>
      <span class="wp-devswitch wp-devswitch--static" ref={staticBox}><span class="wp-devswitch__name" ref={staticName} /></span>
      <div class="wp-menu" role="menu" l:aria-label="home.yourAirPods" hidden ref={menu}
        onkeydown={(e) => {
          let next: number | null = null;
          if (e.key === 'ArrowDown') {
            next = focusAt + 1;
          } else if (e.key === 'ArrowUp') {
            next = focusAt - 1;
          } else if (e.key === 'Home') {
            next = 0;
          } else if (e.key === 'End') {
            next = items.length;
          } else if (e.key === 'Tab') {
            close(false);
          } else if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            close(true);
          }
          if (next !== null) {
            e.preventDefault();
            focusItem(next);
          }
        }}>
        <div class="wp-menu__label" l:text="home.yourAirPods" />
        <div class="wp-menu__sep" role="separator" ref={sep} />
        <button type="button" class="wp-menu__item wp-menu__item--compact" role="menuitem" tabindex="-1" ref={pair}
          onfocus={() => {
            focusAt = items.length;
          }}
          onclick={() => {
            close(false);
            o.onPair();
          }}>
          <Icon name="plus" class="wp-menu__glyph" />
          <span class="wp-menu__title" l:text="menu.pairNew" />
          <Icon name="external" class="wp-menu__ext" />
        </button>
      </div>
    </div>
  ) as HTMLDivElement;

  document.addEventListener('pointerdown', (e) => {
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
        buildItems();
      }
    },
  };
};
