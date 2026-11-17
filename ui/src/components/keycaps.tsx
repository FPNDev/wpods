/* Keycaps for Windows virtual-key codes and MOD_* flags. Win, Shift, Enter, Backspace and the arrows show their
   symbols; punctuation keys show the character the host (or the key event) reports for the current layout. */
import { t, type LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';

export const MOD_ALT = 1;
export const MOD_CTRL = 2;
export const MOD_SHIFT = 4;
export const MOD_WIN = 8;

export type Cap = { label: string; icon: IconName | null; text: string | null };

const NAMED: Record<number, { key: LabelKey; icon: IconName | null }> = {
  0x08: { key: 'key.backspace', icon: 'backspace' },
  0x09: { key: 'key.tab', icon: null },
  0x0d: { key: 'key.enter', icon: 'enter' },
  0x13: { key: 'key.pause', icon: null },
  0x20: { key: 'key.space', icon: null },
  0x21: { key: 'key.pageUp', icon: null },
  0x22: { key: 'key.pageDown', icon: null },
  0x23: { key: 'key.end', icon: null },
  0x24: { key: 'key.home', icon: null },
  0x25: { key: 'key.left', icon: 'arrow-left' },
  0x26: { key: 'key.up', icon: 'arrow-up' },
  0x27: { key: 'key.right', icon: 'arrow-right' },
  0x28: { key: 'key.down', icon: 'arrow-down' },
  0x2d: { key: 'key.insert', icon: null },
  0x2e: { key: 'key.delete', icon: null },
};
const NUMPAD_OPS: Record<number, string> = {
  0x6a: '*',
  0x6b: '+',
  0x6d: '-',
  0x6e: '.',
  0x6f: '/',
};

export const isPunctuation = (vk: number): boolean =>
  (vk >= 0xba && vk <= 0xc0) || (vk >= 0xdb && vk <= 0xdf) || vk === 0xe2;

const plain = (text: string): Cap => ({ label: text, icon: null, text });

/** The main key's cap; null for keys a hotkey can't use (modifiers, Esc, media keys and the like). */
export const keyCap = (vk: number, char: string | null): Cap | null => {
  if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5a)) {
    return plain(String.fromCodePoint(vk));
  }
  if (vk >= 0x60 && vk <= 0x69) {
    return plain(t('key.num', { key: vk - 0x60 }));
  }
  if (vk in NUMPAD_OPS) {
    return plain(t('key.num', { key: NUMPAD_OPS[vk] }));
  }
  if (vk >= 0x70 && vk <= 0x87) {
    return plain(`F${vk - 0x6f}`);
  }
  const named = NAMED[vk] as (typeof NAMED)[number] | undefined;
  if (named) {
    const label = t(named.key);

    return { label, icon: named.icon, text: named.icon ? null : label };
  }

  // A dead key reports no character while recording; the host's reply brings it.
  return isPunctuation(vk) ? plain(char ?? '?') : null;
};

/** Modifier caps in Windows' order: Win, Ctrl, Alt, Shift. */
export const modCaps = (mods: number): Cap[] => {
  const caps: Cap[] = [];
  if (mods & MOD_WIN) {
    caps.push({ label: t('key.win'), icon: 'win', text: null });
  }
  if (mods & MOD_CTRL) {
    caps.push(plain(t('key.ctrl')));
  }
  if (mods & MOD_ALT) {
    caps.push(plain(t('key.alt')));
  }
  if (mods & MOD_SHIFT) {
    caps.push({ label: t('key.shift'), icon: 'shift', text: t('key.shift') });
  }

  return caps;
};

const Key = (cap: Cap): HTMLElement => {
  const key = (<kbd class="wp-key" />) as HTMLElement;
  if (cap.icon) {
    key.append(<Icon name={cap.icon} class="wp-icon--sm" />);
  }
  if (cap.text) {
    key.append(cap.text);
  }

  return key;
};

export const renderCaps = (el: HTMLElement, caps: Cap[]): void => {
  el.replaceChildren(...caps.map((cap) => Key(cap)));
};

/** "Win + Alt + N", for screen readers. */
export const capsLabel = (caps: Cap[]): string => {
  let label = '';
  for (const cap of caps) {
    label += label ? ` + ${cap.label}` : cap.label;
  }

  return label;
};
