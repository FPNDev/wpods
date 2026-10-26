/* Controls: double-press speed, press-and-hold duration, the modes press-and-hold cycles through, tone volume
   (each shown only when supported). */
import { createChoiceList } from "../components/choiceList";
import { type Busy, createBusy } from "../components/common";
import { createSegmented, type Segmented } from "../components/segmented";
import { setDisabled, setHidden, setText } from "../dom";
import { post } from "../host";
import { labelsVersion, t, type LabelKey } from "../i18n";
import { Icon, type IconName } from "../icons";
import type { HoldDuration, PressSpeed, StemMode } from "../protocol";
import { capsOf, current, live, store } from "../state";
import type { Ctx, Page } from "./page";

type SegRow<V extends string> = {
  row: HTMLDivElement;
  busy: Busy;
  seg: Segmented<V>;
};

const STEM_ORDER: StemMode[] = [
  "off",
  "noiseCancellation",
  "transparency",
  "adaptive",
];
const STEM_MIN = 2;
const DUCK_MIN = 10;
const DUCK_MAX = 90;
const TONE_MIN = 15;
const TONE_MAX = 100;

const segRow = <V extends string>(
  icon: IconName,
  title: LabelKey,
  desc: LabelKey,
  seg: Segmented<V>,
): SegRow<V> => {
  const busy = createBusy(seg.el, seg.el.elements);
  const row = (
    <div class="wp-row wp-row--stack">
      <div class="wp-row__head">
        <span class="wp-row__lead">
          <Icon name={icon} />
        </span>
        <div class="wp-row__text">
          <span class="wp-row__title" l:text={title} />
          <span class="wp-row__desc" l:text={desc} />
        </div>
        {busy.spin}
      </div>
      {seg.el}
    </div>
  ) as HTMLDivElement;

  return { row, busy, seg };
};

export const createControls = (ctx: Ctx): Page => {
  const press = segRow(
    "double",
    "controls.pressSpeed",
    "controls.pressSpeedDesc",
    createSegmented<PressSpeed>({
      name: "pressSpeed",
      legend: "controls.pressSpeed",
      items: [
        { value: "default", label: "controls.default" },
        { value: "slower", label: "controls.slower" },
        { value: "slowest", label: "controls.slowest" },
      ],
      onChange: (value) => {
        if (store.state) {
          store.state.pressSpeed = value;
        }
        post({ type: "setPressSpeed", value });
      },
    }),
  );
  const hold = segRow(
    "hold",
    "controls.holdDuration",
    "controls.holdDurationDesc",
    createSegmented<HoldDuration>({
      name: "holdDuration",
      legend: "controls.holdDuration",
      items: [
        { value: "default", label: "controls.default" },
        { value: "shorter", label: "controls.shorter" },
        { value: "shortest", label: "controls.shortest" },
      ],
      onChange: (value) => {
        if (store.state) {
          store.state.holdDuration = value;
        }
        post({ type: "setHoldDuration", value });
      },
    }),
  );
  /* Press-and-hold cycle. The Off row shows only while Allow Off isn't known to be off. At least STEM_MIN modes stay
     checked: a click that would leave fewer is ignored, without a message (iPhone behaviour). Rows show in the stem's
     own cycle order. */
  const stemChecked = new Set<StemMode>();
  const stem = createChoiceList<StemMode>({
    items: [
      { value: "off", label: "nc.off" },
      { value: "transparency", label: "nc.transparency" },
      { value: "adaptive", label: "nc.adaptive" },
      { value: "noiseCancellation", label: "nc.noiseCancellation" },
    ],
    onPick: (mode) => {
      const d = current();
      const on = !stemChecked.has(mode);
      if (!d || (!on && stemChecked.size <= STEM_MIN)) {
        return;
      }
      const modes: StemMode[] = [];
      for (const m of STEM_ORDER) {
        if (m === mode ? on : stemChecked.has(m)) {
          modes.push(m);
        }
      }
      d.stemModes = modes;
      post({ type: "setStemModes", id: d.id, modes });
      ctx.update("controls");
    },
  });

  let stemGroup!: HTMLElement;
  let stemList!: HTMLDivElement;
  /* Conversation awareness "Lower media to": host setting duckPercent (10..90 step 5); shown with the
     conversationAwareness cap, enabled while the pair is attached (whether Conversation awareness is on or off). */
  let duckGroup!: HTMLElement;
  let duck!: HTMLInputElement;
  let duckValue!: HTMLSpanElement;
  let lastDuck: number | null = null;
  let duckVersion = -1;
  const showDuck = (value: number): void => {
    duck.style.setProperty(
      "--v",
      `${((value - DUCK_MIN) * 100) / (DUCK_MAX - DUCK_MIN)}%`,
    );
    setText(duckValue, t("battery.pct", { pct: value }));
  };
  let pressGroup!: HTMLElement;
  let toneGroup!: HTMLElement;
  let toneHead!: HTMLDivElement;
  let tone!: HTMLInputElement;
  let toneValue!: HTMLSpanElement;
  let lastTone: number | null = null;

  const showTone = (value: string): void => {
    tone.style.setProperty(
      "--v",
      `${((Number(value) - TONE_MIN) * 100) / (TONE_MAX - TONE_MIN)}%`,
    );
    setText(toneValue, value);
  };

  const el = (
    <div class="wp-page">
      <section class="wp-group" ref={pressGroup}>
        <h2 class="wp-group__title" l:text="controls.pressTitle" />
        <div class="wp-list">
          {press.row}
          {hold.row}
        </div>
      </section>
      <section class="wp-group" ref={stemGroup}>
        <h2 class="wp-group__title" id="stemTitle" l:text="stem.title" />
        <div
          class="wp-list"
          role="group"
          aria-labelledby="stemTitle"
          ref={stemList}
        />
      </section>
      <section class="wp-group" ref={duckGroup}>
        <h2 class="wp-group__title" l:text="features.conversation" />
        <div class="wp-list">
          <div class="wp-row wp-row--stack">
            <div class="wp-row__head">
              <div class="wp-row__text">
                <span
                  class="wp-row__title"
                  id="duckTitle"
                  l:text="features.duck"
                />
                <span class="wp-row__desc" l:text="features.duckDesc" />
              </div>
            </div>
            <div class="wp-slider">
              <input
                class="wp-slider__input"
                type="range"
                min={String(DUCK_MIN)}
                max={String(DUCK_MAX)}
                step="5"
                aria-labelledby="duckTitle"
                ref={duck}
                oninput={() => showDuck(Number(duck.value))}
                onchange={() => {
                  const d = current();
                  const value = Number(duck.value);
                  lastDuck = value;
                  if (d) {
                    d.host.duckPercent = value;
                    post({
                      type: "setHostSetting",
                      id: d.id,
                      name: "duckPercent",
                      value,
                    });
                  }
                }}
              />
              <span class="wp-slider__value" ref={duckValue} />
            </div>
          </div>
        </div>
      </section>
      <section class="wp-group" ref={toneGroup}>
        <h2 class="wp-group__title" l:text="controls.soundsTitle" />
        <div class="wp-list">
          <div class="wp-row wp-row--stack">
            <div class="wp-row__head" ref={toneHead}>
              <div class="wp-row__text">
                <span class="wp-row__title" l:text="controls.tone" />
                <span class="wp-row__desc" l:text="controls.toneDesc" />
              </div>
            </div>
            <div class="wp-slider">
              <Icon name="tone" class="wp-slider__icon" />
              <input
                class="wp-slider__input"
                type="range"
                min={String(TONE_MIN)}
                max={String(TONE_MAX)}
                l:aria-label="controls.tone"
                ref={tone}
                oninput={() => showTone(tone.value)}
                onchange={() => {
                  const value = Number(tone.value);
                  lastTone = value;
                  if (store.state) {
                    store.state.toneVolume = value;
                  }
                  post({ type: "setToneVolume", value });
                }}
              />
              <span class="wp-slider__value" ref={toneValue} />
            </div>
          </div>
        </div>
      </section>
    </div>
  ) as HTMLDivElement;

  stemList.append(...stem.rows);
  const toneBusy = createBusy(tone, [tone]);
  toneHead.append(toneBusy.spin);

  const renderStem = (
    modes: StemMode[],
    shown: boolean,
    off: boolean,
    adaptive: boolean,
    locked: boolean,
  ): void => {
    setHidden(stemGroup, !shown);
    if (!shown) {
      return;
    }
    stem.show("off", off);
    stem.show("adaptive", adaptive);
    stemChecked.clear();
    for (const m of modes) {
      stemChecked.add(m);
    }
    stem.lock(locked);
    for (const m of STEM_ORDER) {
      stem.set(m, stemChecked.has(m));
    }
  };

  const renderSeg = <V extends string>(
    r: SegRow<V>,
    shown: boolean,
    locked: boolean,
    checking: boolean,
    notReady: boolean,
    value: V | null,
  ): void => {
    setHidden(r.row, !shown);
    r.busy.set(checking, notReady);
    r.seg.lock(locked);
    r.seg.set(value);
  };

  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
    const pending = !!st && st.checking;
    const notReady = !!st && !st.ready;
    const hasPress = caps.has("pressSpeed");
    const hasHold = caps.has("holdDuration");
    setHidden(pressGroup, !hasPress && !hasHold);
    renderSeg(
      press,
      hasPress,
      !st,
      pending,
      notReady,
      st ? st.pressSpeed : d.saved.pressSpeed,
    );
    renderSeg(
      hold,
      hasHold,
      !st,
      pending,
      notReady,
      st ? st.holdDuration : d.saved.holdDuration,
    );

    renderStem(
      d.stemModes,
      caps.has("listeningMode"),
      !(
        caps.has("allowOff") && (st ? st.allowOff : d.saved.allowOff) === false
      ),
      caps.has("adaptive"),
      !st,
    );

    setHidden(duckGroup, !caps.has("conversationAwareness"));
    setDisabled(duck, !st);
    if (d.host.duckPercent !== lastDuck || duckVersion !== labelsVersion) {
      lastDuck = d.host.duckPercent;
      duckVersion = labelsVersion;
      duck.value = String(lastDuck);
      showDuck(lastDuck);
    }

    setHidden(toneGroup, !caps.has("toneVolume"));
    const value = st ? st.toneVolume : d.saved.toneVolume;
    toneBusy.set(pending, notReady);
    setDisabled(tone, !st);
    if (value !== null && value !== lastTone) {
      lastTone = value;
      tone.value = String(value);
      showTone(tone.value);
    }
    setHidden(toneValue, value === null);
  };

  return { el, sections: { controls: render } };
};
