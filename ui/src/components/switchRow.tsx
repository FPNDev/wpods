/* A list row with a title, a description and a switch. `stack` rows put the text and switch in a head line so
   more content (notes, live values) can follow below; `checking` adds the Checking... slot next to the switch.
   `desc: null` leaves the description to the render (it gets the id `<id>-desc`). */
import { bindText, type LabelKey } from '../i18n';
import { Checking, createSwitch } from './common';

export type SwitchRow = {
  row: HTMLDivElement;
  input: HTMLInputElement;
  sw: HTMLLabelElement;
  desc: HTMLSpanElement;
  check: HTMLSpanElement | null;
};

export const createSwitchRow = (o: {
  id: string;
  title: LabelKey;
  desc: LabelKey | null;
  stack?: boolean;
  checking?: boolean;
  onChange: (on: boolean) => void;
}): SwitchRow => {
  const sw = createSwitch(o.id, o.onChange);
  const desc = <span class="wp-row__desc" /> as HTMLSpanElement;
  if (o.desc) {
    desc.append(bindText(o.desc));
  } else {
    desc.id = `${o.id}-desc`;
  }
  const text = (
    <div class="wp-row__text">
      <span class="wp-row__title" id={o.id} l:text={o.title} />
      {desc}
    </div>
  );
  const check = o.checking ? Checking() : null;
  let row: HTMLDivElement;
  if (o.stack) {
    const head = <div class="wp-row__head">{text}{sw.el}</div>;
    if (check) {
      head.append(check);
    }
    row = <div class="wp-row wp-row--stack">{head}</div> as HTMLDivElement;
  } else {
    row = <div class="wp-row">{text}{sw.el}</div> as HTMLDivElement;
  }
  return { row, input: sw.input, sw: sw.el, desc, check };
};
