/* Weight meter: 4 small bars, filled per weight (light 1 .. veryHeavy 4); notMeasured draws all 4 hollow (dashed).
   Its accessible label is the weight word. */
import type { Weight } from "../protocol";

const FILLED: Record<Weight, number> = {
  light: 1,
  medium: 2,
  heavy: 3,
  veryHeavy: 4,
  notMeasured: 0,
};
const BARS = 4;

export const WeightMeter = (weight: Weight): HTMLSpanElement => {
  const el = (
    <span
      class={
        weight === "notMeasured" ? "wp-meter wp-meter--unmeasured" : "wp-meter"
      }
      role="img"
      l:aria-label={`mic.weight.${weight}`}
    />
  ) as HTMLSpanElement;
  for (let i = 0; i < BARS; i++) {
    el.append(
      <span
        class={i < FILLED[weight] ? "wp-meter__bar is-on" : "wp-meter__bar"}
      />,
    );
  }
  return el;
};
