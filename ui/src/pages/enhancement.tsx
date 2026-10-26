/* Voice enhancement (app-wide, never locked): Off, then the enhancements under their localized names. The row whose
   stage loads shows the spinner in its check slot. Also the radio-row pieces the Mic modes page shares. */
import { setAttr, setClass } from "../dom";
import { post } from "../host";
import { bindText, t, type LabelKey } from "../i18n";
import { Icon, Spinner } from "../icons";
import { store } from "../state";
import type { Ctx, Page } from "./page";

export type Mark = (row: HTMLButtonElement | null) => void;

const enhancementKey = (id: string): LabelKey => `mic.enh.${id}` as LabelKey;
export const enhancementName = (id: string | null): string =>
  t(id === null ? "mic.off" : enhancementKey(id));

export const Choice = (
  title: Text | string,
  onclick: () => void,
  desc?: HTMLSpanElement,
): HTMLButtonElement =>
  (
    <button
      type="button"
      class="wp-row wp-choice"
      role="radio"
      aria-checked="false"
      onclick={onclick}
    >
      <span class="wp-row__text">
        <span class="wp-row__title">{title}</span>
        {desc ?? ""}
      </span>
      <Icon name="check" class="wp-row__check" />
    </button>
  ) as HTMLButtonElement;

export const createCheck = (): Mark => {
  let checked: HTMLButtonElement | null = null;
  return (next) => {
    if (next === checked) {
      return;
    }
    if (checked) {
      setAttr(checked, "aria-checked", "false");
    }
    if (next) {
      setAttr(next, "aria-checked", "true");
    }
    checked = next;
  };
};

export const createLoading = (): Mark => {
  let loading: HTMLButtonElement | null = null;
  const spinner = (<Spinner />) as SVGSVGElement;

  return (next) => {
    if (next === loading) {
      return;
    }
    if (loading) {
      setClass(loading, "is-loading", false);
      setAttr(loading, "aria-busy", null);
    }
    if (next) {
      setClass(next, "is-loading", true);
      setAttr(next, "aria-busy", "true");
      next.insertBefore(spinner, next.lastChild);
    } else {
      spinner.remove();
    }
    loading = next;
  };
};

export const createEnhancement = (ctx: Ctx): Page => {
  let list!: HTMLDivElement;
  const rows = new Map<string, HTMLButtonElement>();
  const check = createCheck();
  const load = createLoading();
  const pick = (id: string | null): void => {
    store.micConfig.enhancement = id;
    post({ type: "setEnhancement", value: id });
    ctx.update("mic");
  };

  const el = (
    <div class="wp-page">
      <section class="wp-group">
        <div
          class="wp-list"
          role="radiogroup"
          l:aria-label="mic.enhancement"
          ref={list}
        />
      </section>
    </div>
  ) as HTMLDivElement;

  rows.set(
    "",
    Choice(bindText("mic.off"), () => pick(null)),
  );
  for (const option of store.micCatalog.enhancement) {
    rows.set(
      option.id,
      Choice(bindText(enhancementKey(option.id)), () => pick(option.id)),
    );
  }
  for (const row of rows.values()) {
    list.append(row);
  }

  const render = (): void => {
    check(rows.get(store.micConfig.enhancement ?? "") ?? null);
    const id = store.micLoading.enhancement;
    load(id === null ? null : (rows.get(id) ?? null));
  };

  return { el, sections: { mic: render } };
};
