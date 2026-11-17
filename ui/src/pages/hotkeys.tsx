/* Hotkeys page: one row per action, grouped by setting. Selecting a row's field records a hotkey (Esc cancels,
   Backspace or Delete clears); the host lets go of every hotkey meanwhile so the keys reach the page. A group or row
   shows when one of the AirPods has the feature, or when it already has a hotkey. */
import { Note } from '../components/common';
import {
  capsLabel,
  isPunctuation,
  keyCap,
  MOD_ALT,
  MOD_CTRL,
  MOD_SHIFT,
  MOD_WIN,
  modCaps,
  renderCaps,
} from '../components/keycaps';
import { createSwitchRow } from '../components/switchRow';
import { setAttr, setChecked, setClass, setHidden, setText } from '../dom';
import { post } from '../host';
import { bindAttr, t, type LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';
import type { Capability, HotkeyAction, Shortcut } from '../protocol';
import { capsOf, store } from '../state';
import type { Ctx, Page } from './page';

type RowDef = {
  action: HotkeyAction;
  title: LabelKey;
  desc?: LabelKey;
  icon?: IconName;
  cap?: Capability;
};
type GroupDef = {
  title: LabelKey;
  cap?: Capability;
  foot?: LabelKey;
  rows: RowDef[];
};
type Row = {
  def: RowDef;
  el: HTMLDivElement;
  field: HTMLButtonElement;
  caps: HTMLSpanElement;
  clear: HTMLButtonElement;
  msg: HTMLParagraphElement;
  msgInfo: SVGSVGElement;
  msgWarn: SVGSVGElement;
  msgText: HTMLSpanElement;
};
type Group = { def: GroupDef; el: HTMLElement; rows: Row[] };

const VK_TAB = 0x09;
const VK_BACK = 0x08;
const VK_ESCAPE = 0x1b;
const VK_DELETE = 0x2e;
const VK_F13 = 0x7c;
const VK_F24 = 0x87;
// Shift, Ctrl, Alt, left and right Win.
const MODIFIER_KEYS = new Set([0x10, 0x11, 0x12, 0x5b, 0x5c]);

const toggle = (
  title: LabelKey,
  on: HotkeyAction,
  off: HotkeyAction,
  extra?: { cap?: Capability; foot?: LabelKey },
): GroupDef => ({
  title,
  ...extra,
  rows: [
    { action: on, title: 'hotkeys.on' },
    { action: off, title: 'hotkeys.off' },
  ],
});

const GROUPS: GroupDef[] = [
  {
    title: 'nc.title',
    cap: 'listeningMode',
    foot: 'hotkeys.againFoot',
    rows: [
      {
        action: 'noise.cycle',
        title: 'hotkeys.next',
        desc: 'hotkeys.nextNoiseDesc',
        icon: 'cycle',
      },
      { action: 'noise.off', title: 'nc.off', icon: 'nc-off' },
      {
        action: 'noise.transparency',
        title: 'nc.transparency',
        icon: 'nc-transparency',
      },
      {
        action: 'noise.adaptive',
        title: 'nc.adaptive',
        icon: 'nc-adaptive',
        cap: 'adaptive',
      },
      {
        action: 'noise.noiseCancellation',
        title: 'nc.noiseCancellation',
        icon: 'nc-anc',
      },
    ],
  },
  {
    title: 'mic.mode',
    foot: 'hotkeys.againFoot',
    rows: [
      {
        action: 'mic.cycle',
        title: 'hotkeys.next',
        desc: 'hotkeys.nextMicDesc',
        icon: 'cycle',
      },
      { action: 'mic.standard', title: 'mic.standard', icon: 'mic-standard' },
      {
        action: 'mic.voiceIsolation',
        title: 'mic.voiceIsolation',
        icon: 'mic-voice',
      },
      {
        action: 'mic.wideSpectrum',
        title: 'mic.wideSpectrum',
        icon: 'mic-wide',
      },
    ],
  },
  toggle('mixer.title', 'mixer.on', 'mixer.off'),
  toggle('windows.latency', 'lowLatency.on', 'lowLatency.off'),
  toggle('hotkeys.handsFree', 'handsFree.on', 'handsFree.off'),
  toggle('features.conversation', 'conversation.on', 'conversation.off', {
    cap: 'conversationAwareness',
  }),
  toggle('features.hearingAid', 'hearingAid.on', 'hearingAid.off', {
    cap: 'hearingAid',
  }),
  toggle('hearing.mediaAssist', 'mediaAssist.on', 'mediaAssist.off', {
    cap: 'mediaAssist',
    foot: 'hotkeys.mediaAssistFoot',
  }),
  {
    title: 'app.name',
    rows: [
      { action: 'window.toggle', title: 'hotkeys.window', icon: 'window' },
    ],
  },
];

const NAMES = new Map<HotkeyAction, { group: LabelKey; row: LabelKey }>();
for (const g of GROUPS) {
  for (const r of g.rows) {
    NAMES.set(r.action, { group: g.title, row: r.title });
  }
}

const settingOf = (action: HotkeyAction): string =>
  action.slice(0, action.indexOf('.'));

// Same setting, or noise control with a mic mode (the AirPods' chime would hide the mic mode's sound).
const clash = (a: string, b: string): boolean =>
  a === b || (a === 'noise' && b === 'mic') || (a === 'mic' && b === 'noise');

const actionName = (action: HotkeyAction): string => {
  const n = NAMES.get(action);

  return n ? t('hotkeys.actionName', { group: t(n.group), action: t(n.row) }) : action;
};

const modsOf = (e: KeyboardEvent): number =>
  (e.altKey ? MOD_ALT : 0) |
  (e.ctrlKey ? MOD_CTRL : 0) |
  (e.shiftKey ? MOD_SHIFT : 0) |
  (e.metaKey ? MOD_WIN : 0);

const anyHas = (cap: Capability): boolean => {
  for (const d of store.devices) {
    if (capsOf(d).has(cap)) {
      return true;
    }
  }

  return false;
};

export const createHotkeys = (ctx: Ctx): Page => {
  let recording: Row | null = null;
  // Modifiers held while recording, shown before the main key comes.
  let held = 0;
  // The last key pressed while recording needs Ctrl, Alt or Win.
  let needsModifier = false;
  const groups: Group[] = [];

  const sounds = createSwitchRow({
    id: 'hkSoundsTitle',
    title: 'hotkeys.sounds',
    desc: 'hotkeys.soundsDesc',
    onChange: (value) => {
      store.hotkeySounds = value;
      post({ type: 'setHotkeySounds', value });
    },
  });

  const stop = (): void => {
    if (!recording) {
      return;
    }
    recording = null;
    post({ type: 'recordHotkey', value: false });
    ctx.update('hotkeys');
  };

  const start = (row: Row): void => {
    if (recording === row) {
      return;
    }
    stop();
    recording = row;
    held = 0;
    needsModifier = false;
    post({ type: 'recordHotkey', value: true });
    ctx.update('hotkeys');
  };

  // Shown at once; the host's answer brings the real status.
  const save = (
    row: Row,
    shortcut: Shortcut | null,
    char: string | null,
  ): void => {
    const action = row.def.action;
    if (shortcut) {
      store.hotkeys.set(action, {
        action,
        ...shortcut,
        char,
        status: 'ok',
        with: [],
      });
    } else {
      store.hotkeys.delete(action);
    }
    post({ type: 'setHotkey', name: action, value: shortcut });
    ctx.update('hotkeys');
  };

  const onKey = (row: Row, e: KeyboardEvent): void => {
    if (recording !== row) {
      return;
    }
    const mods = modsOf(e);
    // On Windows, keyCode is the virtual-key code RegisterHotKey takes, in the current keyboard layout.
    // oxlint-disable-next-line typescript/no-deprecated
    const vk = e.keyCode;
    if (vk === VK_TAB && (mods & (MOD_CTRL | MOD_ALT | MOD_WIN)) === 0) {
      stop();

      return;
    }
    e.preventDefault();
    // Keeps Escape from also going back a page.
    e.stopPropagation();
    if (e.repeat) {
      return;
    }
    if (MODIFIER_KEYS.has(vk)) {
      held = mods;
      needsModifier = false;
      ctx.update('hotkeys');

      return;
    }
    if (mods === 0 && vk === VK_ESCAPE) {
      stop();

      return;
    }
    if (mods === 0 && (vk === VK_BACK || vk === VK_DELETE)) {
      save(row, null, null);
      stop();

      return;
    }
    const char =
      isPunctuation(vk) && e.key.length === 1 ? e.key.toUpperCase() : null;
    if (!keyCap(vk, char)) {
      return;
    }
    if (
      (mods & (MOD_CTRL | MOD_ALT | MOD_WIN)) === 0 &&
      (vk < VK_F13 || vk > VK_F24)
    ) {
      held = mods;
      needsModifier = true;
      ctx.update('hotkeys');

      return;
    }
    save(row, { vk, mods }, char);
    stop();
  };

  const createRow = (def: RowDef): Row => {
    const titleId = `hk-${def.action}`;
    const msgId = `${titleId}-msg`;
    let field!: HTMLButtonElement;
    let caps!: HTMLSpanElement;
    let clear!: HTMLButtonElement;
    let msg!: HTMLParagraphElement;
    let text!: HTMLDivElement;
    let head!: HTMLDivElement;
    let msgText!: HTMLSpanElement;
    const msgInfo = Icon({ name: 'info' });
    const msgWarn = Icon({ name: 'warning' });
    const el = (
      <div class="wp-row wp-row--stack wp-hk">
        <div class="wp-row__head" ref={head}>
          <div class="wp-row__text" ref={text}>
            <span class="wp-row__title" id={titleId} l:text={def.title} />
          </div>
          <span class="wp-hk__end">
            <button
              type="button"
              class="wp-hk__field"
              aria-describedby={msgId}
              ref={field}
            >
              <span class="wp-hk__caps" ref={caps} />
            </button>
            <button
              type="button"
              class="wp-iconbtn wp-hk__clear"
              l:aria-label="hotkeys.clear"
              ref={clear}
            >
              <Icon name="trash" />
            </button>
          </span>
        </div>
        <p class="wp-hk__msg" id={msgId} hidden ref={msg}>
          {msgInfo}
          {msgWarn}
          <span ref={msgText} />
        </p>
      </div>
    ) as HTMLDivElement;
    bindAttr(clear, 'title', 'hotkeys.clear');
    if (def.icon) {
      head.prepend(
        <span class="wp-row__lead">
          <Icon name={def.icon} />
        </span>,
      );
    }
    if (def.desc) {
      text.append(<span class="wp-row__desc" l:text={def.desc} />);
    }
    const row: Row = {
      def,
      el,
      field,
      caps,
      clear,
      msg,
      msgInfo,
      msgWarn,
      msgText,
    };
    field.addEventListener('click', () => {
      start(row);
    });
    field.addEventListener('keydown', (e) => {
      onKey(row, e);
    });
    field.addEventListener('keyup', (e) => {
      if (recording === row) {
        e.preventDefault();
        held = modsOf(e);
        ctx.update('hotkeys');
      }
    });
    field.addEventListener('blur', () => {
      if (recording === row) {
        stop();
      }
    });
    clear.addEventListener('click', () => {
      save(row, null, null);
      field.focus();
    });

    return row;
  };

  const el = (
    <div class="wp-page">
      {Note({ label: 'hotkeys.note' })}
      <section class="wp-group">
        <div class="wp-list">{sounds.row}</div>
      </section>
    </div>
  ) as HTMLDivElement;
  for (const def of GROUPS) {
    let list!: HTMLDivElement;
    const section = (
      <section class="wp-group">
        <h2 class="wp-group__title" l:text={def.title} />
        <div class="wp-list" ref={list} />
      </section>
    ) as HTMLElement;
    if (def.foot) {
      section.append(<p class="wp-group__foot" l:text={def.foot} />);
    }
    const rows: Row[] = [];
    for (const r of def.rows) {
      const row = createRow(r);
      rows.push(row);
      list.append(row.el);
    }
    groups.push({ def, el: section, rows });
    el.append(section);
  }

  const showMsg = (row: Row, text: string | null, warn: boolean): void => {
    setHidden(row.msg, text === null);
    if (text === null) {
      return;
    }
    setText(row.msgText, text);
    setHidden(row.msgInfo, warn);
    setHidden(row.msgWarn, !warn);
    setClass(row.msg, 'wp-hk__msg--warn', warn);
  };

  const names = (actions: HotkeyAction[]): string => {
    const list: string[] = [];
    for (const a of actions) {
      list.push(actionName(a));
    }

    return new Intl.ListFormat(document.documentElement.lang, {
      type: 'conjunction',
    }).format(list);
  };

  const renderRow = (row: Row): void => {
    const action = row.def.action;
    const item = store.hotkeys.get(action);
    const isRecording = recording === row;
    const name = actionName(action);
    setClass(row.field, 'is-recording', isRecording);
    setClass(row.field, 'is-empty', !item && !isRecording);
    setHidden(row.clear, !item || isRecording);
    if (isRecording) {
      if (held) {
        renderCaps(row.caps, modCaps(held));
      } else {
        row.caps.replaceChildren(t('hotkeys.press'));
      }
      setAttr(row.field, 'aria-label', `${name}: ${t('hotkeys.press')}`);
      showMsg(
        row,
        t(needsModifier ? 'hotkeys.needModifier' : 'hotkeys.recordHint'),
        needsModifier,
      );

      return;
    }
    const key = item ? keyCap(item.vk, item.char) : null;
    if (!item || !key) {
      row.caps.replaceChildren(
        Icon({ name: 'plus', class: 'wp-icon--sm' }),
        t('hotkeys.add'),
      );
      setAttr(row.field, 'aria-label', `${name}: ${t('hotkeys.none')}`);
      showMsg(row, null, false);

      return;
    }
    const caps = modCaps(item.mods);
    caps.push(key);
    renderCaps(row.caps, caps);
    setAttr(row.field, 'aria-label', `${name}: ${capsLabel(caps)}`);
    const setting = settingOf(action);
    const same: HotkeyAction[] = [];
    const others: HotkeyAction[] = [];
    for (const a of item.with) {
      (clash(settingOf(a), setting) ? same : others).push(a);
    }
    switch (item.status) {
      case 'conflict':
        showMsg(row, t('hotkeys.conflict', { action: names(same) }), true);
        break;
      case 'taken':
        showMsg(row, t('hotkeys.taken'), true);
        break;
      case 'reserved':
        showMsg(row, t('hotkeys.reserved'), true);
        break;
      case 'toggle':
        showMsg(row, t('hotkeys.toggle'), false);
        break;
      default:
        showMsg(
          row,
          others.length > 0
            ? t('hotkeys.alsoRuns', { action: names(others) })
            : null,
          false,
        );
        break;
    }
  };

  const render = (): void => {
    setChecked(sounds.input, store.hotkeySounds);
    for (const g of groups) {
      let shown = false;
      for (const row of g.rows) {
        const cap = row.def.cap ?? g.def.cap;
        const visible =
          !cap || anyHas(cap) || store.hotkeys.has(row.def.action);
        setHidden(row.el, !visible);
        shown ||= visible;
        if (visible) {
          renderRow(row);
        }
      }
      setHidden(g.el, !shown);
    }
  };

  return {
    el,
    sections: { hotkeys: render, windows: render },
    leave: stop,
  };
};
