import { Note } from '../components/common';
import { createSegmented } from '../components/segmented';
import { createSwitchRow, type SwitchRow } from '../components/switchRow';
import { setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon, Spinner } from '../icons';
import type { Theme } from '../protocol';
import { capsOf, current, live, store, updateText } from '../state';
import { getTheme, getUseAccent, setThemePrefs } from '../theme';
import type { Ctx, Page } from './page';

export const createWindowsSettings = (ctx: Ctx): Page => {
  const hostToggle =
    (name: 'disableHandsFree' | 'lowLatency' | 'lowLatencyMic' | 'hdVoice') =>
    (value: boolean): void => {
      const d = current();
      if (d) {
        d.host[name] = value;
        post({ type: 'setHostSetting', id: d.id, name, value });
        ctx.update('windows');
      }
    };
  let forDevice!: HTMLHeadingElement;
  let langValue!: HTMLSpanElement;
  let toLanguage!: HTMLButtonElement;
  let toMicModes!: HTMLButtonElement;
  let toMixer!: HTMLButtonElement;
  let toHotkeys!: HTMLButtonElement;
  const handsFree = createSwitchRow({
    id: 'hfpTitle',
    title: 'windows.handsFree',
    desc: 'windows.handsFreeDesc',
    onChange: hostToggle('disableHandsFree'),
  });
  // Windows takes a few seconds to apply a change, so a spinner takes the switch's place meanwhile.
  const handsFreeSwitch = handsFree.row.lastElementChild as HTMLElement;
  const handsFreeSpin = (
    <span
      class="wp-switch-spin"
      role="img"
      l:aria-label="common.checking"
      hidden
    >
      <Spinner class="wp-spinner--sm" />
    </span>
  ) as HTMLSpanElement;
  handsFree.row.append(handsFreeSpin);
  const hdVoice = createSwitchRow({
    id: 'hdVoiceTitle',
    title: 'windows.hdVoice',
    desc: 'windows.hdVoiceDesc',
    onChange: hostToggle('hdVoice'),
  });
  const handsFreeNoteRow = (
    <div class="wp-row" hidden>
      {Note({ label: 'windows.handsFreeNoStereoMic', warning: true })}
    </div>
  ) as HTMLDivElement;
  const latency = createSwitchRow({
    id: 'latencyTitle',
    title: 'windows.latency',
    desc: 'windows.latencyDesc',
    stack: true,
    onChange: hostToggle('lowLatency'),
  });
  const latencyNote = Note({ label: 'windows.latencyNote', warning: true });
  latencyNote.hidden = true;
  latency.row.append(latencyNote);
  const latencyMic = createSwitchRow({
    id: 'latencyMicTitle',
    title: 'windows.latencyMic',
    desc: 'windows.latencyMicDesc',
    stack: true,
    onChange: hostToggle('lowLatencyMic'),
  });
  const latencyMicNote = Note({
    label: 'windows.latencyMicNote',
    warning: true,
  });
  latencyMicNote.hidden = true;
  const latencyMicHdNote = Note({
    label: 'windows.latencyMicHdNote',
    warning: true,
  });
  latencyMicHdNote.hidden = true;
  latencyMic.row.append(latencyMicNote, latencyMicHdNote);
  const startup = createSwitchRow({
    id: 'startupTitle',
    title: 'windows.startup',
    desc: 'windows.startupDesc',
    onChange: (value) => {
      store.startup = value;
      post({ type: 'setStartup', value });
    },
  });
  const accent = createSwitchRow({
    id: 'accentTitle',
    title: 'windows.accent',
    desc: 'windows.accentDesc',
    onChange: (value) => {
      setThemePrefs({ useAccent: value });
      post({ type: 'setAccent', value });
    },
  });
  const autoUpdate = createSwitchRow({
    id: 'autoUpdateTitle',
    title: 'update.auto',
    desc: 'update.autoDesc',
    onChange: (value) => {
      store.update.auto = value;
      post({ type: 'setAutoUpdate', value });
    },
  });
  let versionText!: HTMLSpanElement;
  let updateStatus!: HTMLSpanElement;
  let updateBtn!: HTMLButtonElement;
  const theme = createSegmented<Theme>({
    name: 'theme',
    legend: 'windows.theme',
    items: [
      { value: 'light', label: 'windows.themeLight' },
      { value: 'dark', label: 'windows.themeDark' },
      { value: 'auto', label: 'windows.themeAuto' },
    ],
    onChange: (value) => {
      setThemePrefs({ theme: value });
      post({ type: 'setTheme', theme: value });
    },
  });

  const el = (
    <div class="wp-page">
      <section class="wp-group">
        <h2 class="wp-group__title" ref={forDevice} />
        <div class="wp-list">
          {handsFree.row}
          {handsFreeNoteRow}
          {latency.row}
          {latencyMic.row}
          {hdVoice.row}
        </div>
      </section>
      <section class="wp-group">
        <h2 class="wp-group__title" l:text="app.name" />
        <div class="wp-list">
          <button
            type="button"
            class="wp-row wp-row--nav"
            ref={toMicModes}
            onclick={() => {
              ctx.go('micModes');
            }}
          >
            <span class="wp-row__text">
              <span class="wp-row__title" l:text="mic.modes" />
              <span class="wp-row__desc" l:text="mic.modesDesc" />
            </span>
            <span class="wp-row__end">
              <Icon name="chevron-right" />
            </span>
          </button>
          <button
            type="button"
            class="wp-row wp-row--nav"
            ref={toMixer}
            onclick={() => {
              ctx.go('mixer');
            }}
          >
            <span class="wp-row__text">
              <span class="wp-row__title" l:text="mixer.title" />
              <span class="wp-row__desc" l:text="mixer.desc" />
            </span>
            <span class="wp-row__end">
              <Icon name="chevron-right" />
            </span>
          </button>
          <button
            type="button"
            class="wp-row wp-row--nav"
            ref={toHotkeys}
            onclick={() => {
              ctx.go('hotkeys');
            }}
          >
            <span class="wp-row__text">
              <span class="wp-row__title" l:text="hotkeys.title" />
              <span class="wp-row__desc" l:text="hotkeys.desc" />
            </span>
            <span class="wp-row__end">
              <Icon name="chevron-right" />
            </span>
          </button>
          {startup.row}
          <div class="wp-row wp-row--stack">
            <div class="wp-row__head">
              <div class="wp-row__text">
                <span class="wp-row__title" l:text="windows.theme" />
                <span class="wp-row__desc" l:text="windows.themeDesc" />
              </div>
            </div>
            {theme.el}
          </div>
          <button
            type="button"
            class="wp-row wp-row--nav"
            ref={toLanguage}
            onclick={() => {
              ctx.go('language');
            }}
          >
            <span class="wp-row__text">
              <span class="wp-row__title" l:text="lang.title" />
            </span>
            <span class="wp-row__end">
              <span class="wp-row__value" ref={langValue} />
              <Icon name="chevron-right" />
            </span>
          </button>
          {accent.row}
        </div>
      </section>
      <section class="wp-group">
        <h2 class="wp-group__title" l:text="update.title" />
        <div class="wp-list">
          <div class="wp-row">
            <div class="wp-row__text">
              <span class="wp-row__title" ref={versionText} />
              <span class="wp-row__desc" role="status" ref={updateStatus} />
            </div>
            <span class="wp-row__end">
              <button
                type="button"
                class="wp-btn wp-btn--secondary"
                ref={updateBtn}
                onclick={() => {
                  post({
                    type: store.update.available ? 'updateNow' : 'checkUpdate',
                  });
                }}
              />
            </span>
          </div>
          {autoUpdate.row}
        </div>
      </section>
    </div>
  ) as HTMLDivElement;

  const renderHost = (row: SwitchRow, on: boolean, locked: boolean): void => {
    setChecked(row.input, on);
    setDisabled(row.input, locked);
  };
  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const locked = !live(d);
    setText(forDevice, t('windows.forDevice', { name: d.name }));
    const caps = capsOf(d);
    const hdVoiceOffered = caps.has('hdVoice');
    // HD voice keeps Windows' Hands-Free off.
    const hdVoiceOn = hdVoiceOffered && d.host.hdVoice;
    const applying = store.handsFreeApplying.has(d.id);
    renderHost(
      handsFree,
      hdVoiceOn || d.host.disableHandsFree,
      locked || hdVoiceOn,
    );
    setHidden(handsFreeSwitch, applying);
    setHidden(handsFreeSpin, !applying);
    setHidden(hdVoice.row, !hdVoiceOffered);
    renderHost(hdVoice, d.host.hdVoice, locked || applying);
    // Only while Hands-Free mode is off and nothing else carries the mic.
    setHidden(
      handsFreeNoteRow,
      !caps.has('noStereoMic') || hdVoiceOn || !d.host.disableHandsFree,
    );
    renderHost(latency, d.host.lowLatency, locked);
    setHidden(latencyNote, !d.host.lowLatency);
    // Only for a pair whose mic comes through WPods.
    setHidden(latencyMic.row, caps.has('noStereoMic') && !hdVoiceOn);
    renderHost(latencyMic, d.host.lowLatencyMic, locked);
    setHidden(latencyMicNote, !d.host.lowLatencyMic);
    setHidden(latencyMicHdNote, !d.host.lowLatencyMic || !hdVoiceOn);
    setChecked(startup.input, store.startup);
    theme.set(getTheme());
    setChecked(accent.input, getUseAccent());
    const lang = store.languageByCode.get(store.language);
    setText(langValue, lang ? lang.originalName : t('lang.sameAsWindows'));
  };

  const renderUpdate = (): void => {
    const u = store.update;
    const busy =
      u.status === 'checking' ||
      u.status === 'downloading' ||
      u.status === 'installing';
    setText(versionText, t('update.version', { version: u.version }));
    // "Update now" restarts the service, which pauses Bluetooth.
    setText(
      updateStatus,
      u.available && !busy
        ? `${updateText()} ${t('update.nowDesc')}`
        : updateText(),
    );
    setText(updateBtn, t(u.available ? 'update.now' : 'update.check'));
    setDisabled(updateBtn, busy);
    setChecked(autoUpdate.input, u.auto);
  };

  return {
    el,
    sections: { windows: render, update: renderUpdate },
    returnFocus: (from) =>
      from === 'language'
        ? toLanguage
        : from === 'micModes'
          ? toMicModes
          : from === 'mixer'
            ? toMixer
            : from === 'hotkeys'
              ? toHotkeys
              : null,
  };
};
