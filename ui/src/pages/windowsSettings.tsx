import { Note } from "../components/common";
import { createSegmented } from "../components/segmented";
import { createSwitchRow, type SwitchRow } from "../components/switchRow";
import { setChecked, setDisabled, setHidden, setText } from "../dom";
import { post } from "../host";
import { t } from "../i18n";
import { Icon, Spinner } from "../icons";
import type { Theme } from "../protocol";
import { current, live, store } from "../state";
import { getTheme, getUseAccent, setThemePrefs } from "../theme";
import type { Ctx, Page } from "./page";

export const createWindowsSettings = (ctx: Ctx): Page => {
  const hostToggle =
    (name: "disableHandsFree" | "lowLatency") =>
    (value: boolean): void => {
      const d = current();
      if (d) {
        d.host[name] = value;
        post({ type: "setHostSetting", id: d.id, name, value });
        ctx.update("windows");
      }
    };
  let forDevice!: HTMLHeadingElement;
  let langValue!: HTMLSpanElement;
  let toLanguage!: HTMLButtonElement;
  let toMicModes!: HTMLButtonElement;
  const handsFree = createSwitchRow({
    id: "hfpTitle",
    title: "windows.handsFree",
    desc: "windows.handsFreeDesc",
    onChange: hostToggle("disableHandsFree"),
  });
  // While the host applies a change (Windows takes a few seconds) a spinner takes the switch's place.
  const handsFreeSwitch = handsFree.row.lastElementChild as HTMLElement;
  const handsFreeSpin = (
    <span class="wp-switch-spin" role="img" l:aria-label="common.checking" hidden>
      <Spinner class="wp-spinner--sm" />
    </span>
  ) as HTMLSpanElement;
  handsFree.row.append(handsFreeSpin);
  const latency = createSwitchRow({
    id: "latencyTitle",
    title: "windows.latency",
    desc: "windows.latencyDesc",
    stack: true,
    onChange: hostToggle("lowLatency"),
  });
  const latencyNote = Note({ label: "windows.latencyNote", warning: true });
  latencyNote.hidden = true;
  latency.row.append(latencyNote);
  const startup = createSwitchRow({
    id: "startupTitle",
    title: "windows.startup",
    desc: "windows.startupDesc",
    onChange: (value) => {
      store.startup = value;
      post({ type: "setStartup", value });
    },
  });
  const accent = createSwitchRow({
    id: "accentTitle",
    title: "windows.accent",
    desc: "windows.accentDesc",
    onChange: (value) => {
      setThemePrefs({ useAccent: value });
      post({ type: "setAccent", value });
    },
  });
  const theme = createSegmented<Theme>({
    name: "theme",
    legend: "windows.theme",
    items: [
      { value: "light", label: "windows.themeLight" },
      { value: "dark", label: "windows.themeDark" },
      { value: "auto", label: "windows.themeAuto" },
    ],
    onChange: (value) => {
      setThemePrefs({ theme: value });
      post({ type: "setTheme", theme: value });
    },
  });

  const el = (
    <div class="wp-page">
      <section class="wp-group">
        <h2 class="wp-group__title" ref={forDevice} />
        <div class="wp-list">
          {handsFree.row}
          {latency.row}
        </div>
      </section>
      <section class="wp-group">
        <h2 class="wp-group__title" l:text="app.name" />
        <div class="wp-list">
          <button
            type="button"
            class="wp-row wp-row--nav"
            ref={toMicModes}
            onclick={() => ctx.go("micModes")}
          >
            <span class="wp-row__text">
              <span class="wp-row__title" l:text="mic.modes" />
              <span class="wp-row__desc" l:text="mic.modesDesc" />
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
            onclick={() => ctx.go("language")}
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
    </div>
  ) as HTMLDivElement;

  const renderHost =(row: SwitchRow, on: boolean, locked: boolean): void => {
    setChecked(row.input, on);
    setDisabled(row.input, locked);
  };
  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const locked = !live(d);
    setText(forDevice, t("windows.forDevice", { name: d.name }));
    renderHost(handsFree, d.host.disableHandsFree, locked);
    const applying = store.handsFreeApplying.has(d.id);
    setHidden(handsFreeSwitch, applying);
    setHidden(handsFreeSpin, !applying);
    renderHost(latency, d.host.lowLatency, locked);
    setHidden(latencyNote, !d.host.lowLatency);
    setChecked(startup.input, store.startup);
    theme.set(getTheme());
    setChecked(accent.input, getUseAccent());
    const lang = store.languageByCode.get(store.language);
    setText(langValue, lang ? lang.originalName : t("lang.sameAsWindows"));
  };

  return {
    el,
    sections: { windows: render },
    returnFocus: (from) =>
      from === "language"
        ? toLanguage
        : from === "micModes"
          ? toMicModes
          : null,
  };
};
