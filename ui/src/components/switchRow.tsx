/* A list row with a title, a description and a switch. `stack` rows put the text and switch in a head line so
   more content (notes, live values) can follow below; `checking` adds `busy` (blocks the switch; its spinner
   is not placed, the page shows one for the whole group).
   `desc: null` leaves the description to the render (it gets the id `<id>-desc`). */
import { bindText, type LabelKey } from '../i18n';
import { type Busy, createBusy, createSwitch } from './common';

export type SwitchRow = {
  row: HTMLDivElement;
  input: HTMLInputElement;
  desc: HTMLSpanElement;
  busy: Busy | null;
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
  const desc = (<span class="wp-row__desc" />) as HTMLSpanElement;
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
  const busy = o.checking ? createBusy(sw.input, [sw.input]) : null;
  let row: HTMLDivElement;
  if (o.stack) {
    const head = (
      <div class="wp-row__head">
        {text}
        {sw.el}
      </div>
    );
    row = (<div class="wp-row wp-row--stack">{head}</div>) as HTMLDivElement;
  } else {
    row = (
      <div class="wp-row">
        {text}
        {sw.el}
      </div>
    ) as HTMLDivElement;
  }
  return { row, input: sw.input, desc, busy };
};
