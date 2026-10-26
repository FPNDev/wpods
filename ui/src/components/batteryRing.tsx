import { setAttr, setHidden, setText } from "../dom";
import { labelsVersion, t } from "../i18n";
import { Glyph, Icon } from "../icons";
import type { Batteries, Battery } from "../protocol";

type Part = "left" | "right" | "case" | "max";
type CaseGlyph = "case-pro" | "case";
type Ring = {
  update: (bat: Battery, ear: string, caseGlyph: CaseGlyph) => void;
};
type BatteryRings = {
  el: HTMLDivElement;
  update: (
    bat: Batteries | null,
    ears: { left: string; right: string },
    caseGlyph: CaseGlyph,
  ) => boolean;
};

const RING_C = 2 * Math.PI * 23;
const GLYPH = { left: "bud-left", right: "bud-right", max: "max" } as const;

const createRing = (
  part: Part,
  compact: boolean,
  parent: HTMLElement,
): Ring => {
  let fill!: SVGCircleElement;
  let bolt!: HTMLDivElement;
  let pct!: HTMLDivElement;
  let caseGlyph: CaseGlyph = "case-pro";
  let glyph: Element = (
    <Glyph name={part === "case" ? caseGlyph : GLYPH[part]} />
  );
  let key = "";
  const el = (
    <div class="wp-battery" role="img" hidden>
      <div class="wp-ring">
        <svg viewBox="0 0 52 52" aria-hidden="true">
          <circle class="wp-ring__track" cx="26" cy="26" r="23" />
          <circle class="wp-ring__fill" cx="26" cy="26" r="23" ref={fill} />
        </svg>
        <div class="wp-ring__glyph">{glyph}</div>
        <div class="wp-ring__bolt" hidden ref={bolt}>
          <Icon name="bolt" />
        </div>
      </div>
      <div class="wp-battery__pct" aria-hidden="true" ref={pct} />
    </div>
  ) as HTMLDivElement;
  parent.append(el);
  let label: HTMLDivElement | null = null;
  let earLine: HTMLDivElement | null = null;
  if (!compact) {
    label = (
      <div class="wp-battery__label" aria-hidden="true" />
    ) as HTMLDivElement;
    earLine = (
      <div class="wp-battery__ear" aria-hidden="true" hidden />
    ) as HTMLDivElement;
    el.append(label, earLine);
  }

  return {
    update: (bat, ear, nextCase) => {
      setHidden(el, !bat);
      if (!bat) {
        return;
      }
      if (part === "case" && nextCase !== caseGlyph) {
        caseGlyph = nextCase;
        const next = <Glyph name={nextCase} />;
        glyph.replaceWith(next);
        glyph = next;
      }
      const nextKey = `${bat.pct}|${bat.charging}|${ear}|${labelsVersion}`;
      if (nextKey === key) {
        return;
      }
      key = nextKey;
      const level = bat.charging
        ? "charging"
        : bat.pct < 10
          ? "low"
          : bat.pct < 30
            ? "warn"
            : "ok";
      el.className = `wp-battery wp-battery--${level}`;
      fill.setAttribute(
        "stroke-dasharray",
        `${((RING_C * Math.max(0, Math.min(100, bat.pct))) / 100).toFixed(2)} ${RING_C.toFixed(2)}`,
      );
      setHidden(bolt, !bat.charging);
      const name = t(`battery.${part}`);
      setText(pct, t("battery.pct", { pct: bat.pct }));
      if (label && earLine) {
        const second =
          part === "case" ? (bat.charging ? t("battery.charging") : "") : ear;
        setText(label, name);
        setText(earLine, second);
        setHidden(earLine, !second);
      }
      let sr = t("battery.sr", { part: name, pct: bat.pct });
      if (bat.charging) {
        sr += `, ${t("battery.charging").toLowerCase()}`;
      }
      if (ear) {
        sr += `, ${ear.toLowerCase()}`;
      }
      setAttr(el, "aria-label", sr);
    },
  };
};

export const createBatteries = (compact: boolean): BatteryRings => {
  const el = (
    <div
      class={compact ? "wp-batteries wp-batteries--compact" : "wp-batteries"}
    />
  ) as HTMLDivElement;
  const left = createRing("left", compact, el);
  const right = createRing("right", compact, el);
  const box = createRing("case", compact, el);
  const max = createRing("max", compact, el);
  return {
    el,
    update: (bat, ears, caseGlyph) => {
      left.update(bat?.left ?? null, ears.left, caseGlyph);
      right.update(bat?.right ?? null, ears.right, caseGlyph);
      box.update(bat?.case ?? null, "", caseGlyph);
      max.update(bat?.headphone ?? null, "", caseGlyph);
      return !!bat && !!(bat.left || bat.right || bat.case || bat.headphone);
    },
  };
};
