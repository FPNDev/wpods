import { Choice, createChoiceList } from "../components/choiceList";
import { Note } from "../components/common";
import { WeightMeter } from "../components/weightMeter";
import { setText } from "../dom";
import { post } from "../host";
import { bindText, labelsVersion, t } from "../i18n";
import type { MicMode, MicOption } from "../protocol";
import { applyMicConfig, current, effectiveMode, live, store } from "../state";
import { createCheck, createLoading, type Mark } from "./enhancement";
import type { Ctx, Page } from "./page";

type Hint = { el: HTMLSpanElement; option: MicOption };
type ModelGroup = { rows: Map<string, HTMLButtonElement>; check: Mark };

const MODES: MicMode[] = ["standard", "voiceIsolation", "wideSpectrum"];
const MODE_MIN = 1;
const RAW = "raw";
// MMAC/s from which the hint shows GMAC/s.
const GMAC = 1000;

export const createMicModes = (ctx: Ctx): Page => {
  const modes = createChoiceList<MicMode>({
    items: [
      { value: "standard", label: "mic.standard" },
      { value: "voiceIsolation", label: "mic.voiceIsolation" },
      { value: "wideSpectrum", label: "mic.wideSpectrum" },
    ],
    onPick: (mode) => {
      const enabled = store.micEnabled;
      const on = !enabled.has(mode);
      if (!on && enabled.size <= MODE_MIN) {
        return;
      }
      const next: MicMode[] = [];
      for (const m of MODES) {
        if (m === mode ? on : enabled.has(m)) {
          next.push(m);
        }
      }
      applyMicConfig({ ...store.micConfig, modes: next });
      post({ type: "setMicModes", modes: next });
      ctx.update("mic");
    },
  });

  const groups = {} as Record<MicMode, ModelGroup>;
  const hints: Hint[] = [];
  const load = createLoading();
  let hintsVersion = -1;

  let modeList!: HTMLDivElement;
  const el = (
    <div class="wp-page">
      {Note({ label: "mic.note" })}
      <section class="wp-group">
        <h2
          class="wp-group__title"
          id="micModesTitle"
          l:text="mic.modesGroup"
        />
        <div
          class="wp-list"
          role="group"
          aria-labelledby="micModesTitle"
          ref={modeList}
        />
        <p class="wp-group__foot" l:text="mic.modesGroupDesc" />
      </section>
    </div>
  ) as HTMLDivElement;
  modeList.append(...modes.rows);

  for (const mode of MODES) {
    const titleId = `${mode}ModelsTitle`;
    let list!: HTMLDivElement;
    el.append(
      <section class="wp-group">
        <h2 class="wp-group__title" id={titleId} l:text={`mic.${mode}`} />
        <div
          class="wp-list"
          role="radiogroup"
          aria-labelledby={titleId}
          ref={list}
        />
      </section>,
    );
    const rows = new Map<string, HTMLButtonElement>();
    for (const option of store.micCatalog[mode]) {
      const pick = (): void => {
        store.micConfig.models[mode] = option.id;
        post({ type: "setMicModel", mode, name: option.id });
        ctx.update("mic");
      };
      let row: HTMLButtonElement;
      if (option.id === RAW) {
        row = Choice(
          "radio",
          bindText("mic.raw"),
          pick,
          (
            <span class="wp-row__desc">{bindText("mic.rawDesc")}</span>
          ) as HTMLSpanElement,
        );
      } else {
        const hint = (<span />) as HTMLSpanElement;
        hints.push({ el: hint, option });
        row = Choice(
          "radio",
          option.name,
          pick,
          (
            <span class="wp-row__desc wp-weight">
              {WeightMeter(option.weight)}
              {hint}
            </span>
          ) as HTMLSpanElement,
        );
      }
      rows.set(option.id, row);
      list.append(row);
    }
    groups[mode] = { rows, check: createCheck() };
  }

  const renderHints = (): void => {
    const format = new Intl.NumberFormat(document.documentElement.lang, {
      maximumFractionDigits: 1,
    });
    for (const { el: hint, option } of hints) {
      const delay = format.format(option.latencyMs);
      const threads = option.threads;
      const mmacs = option.mmacs;
      let compute: string;
      if (mmacs === 0) {
        compute = t("mic.computeDsp");
      } else if (mmacs >= GMAC) {
        compute = `${format.format(mmacs / GMAC)} GMAC/s`;
      } else {
        compute = `${format.format(mmacs)} MMAC/s`;
      }
      setText(
        hint,
        threads > 1
          ? t("mic.usageThreads", { threads, compute, delay })
          : t("mic.usage", { compute, delay }),
      );
    }
  };

  const render = (): void => {
    if (hintsVersion !== labelsVersion) {
      hintsVersion = labelsVersion;
      renderHints();
    }
    const models = store.micConfig.models;
    for (const mode of MODES) {
      modes.set(mode, store.micEnabled.has(mode));
      const group = groups[mode];
      group.check(group.rows.get(models[mode]) ?? null);
    }
    const d = current();
    const loadingId = store.micLoading.mode;
    load(
      d && live(d) && loadingId !== null
        ? (groups[effectiveMode(d.host.micMode)].rows.get(loadingId) ?? null)
        : null,
    );
  };

  return { el, sections: { mic: render } };
};
