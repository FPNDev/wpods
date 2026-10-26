import { setAttr, setDisabled, setHidden } from "../dom";
import { bindText, type LabelKey } from "../i18n";
import { Icon } from "../icons";

type ChoiceItem<V extends string> = {
  value: V;
  label: LabelKey;
};
type ChoiceList<V extends string> = {
  rows: HTMLButtonElement[];
  set: (value: V, on: boolean) => void;
  lock: (disabled: boolean) => void;
  show: (value: V, shown: boolean) => void;
};

export const Choice = (
  role: "radio" | "checkbox",
  title: Text | string,
  onclick: () => void,
  desc?: HTMLSpanElement,
): HTMLButtonElement =>
  (
    <button
      type="button"
      class="wp-row wp-choice"
      role={role}
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

export const createChoiceList = <V extends string>(o: {
  items: ChoiceItem<V>[];
  onPick: (value: V) => void;
}): ChoiceList<V> => {
  const byValue = new Map<V, HTMLButtonElement>();
  const rows: HTMLButtonElement[] = [];
  for (const item of o.items) {
    const row = Choice("checkbox", bindText(item.label), () =>
      o.onPick(item.value),
    );
    byValue.set(item.value, row);
    rows.push(row);
  }
  return {
    rows,
    set: (value, on) => {
      const row = byValue.get(value);
      if (row) {
        setAttr(row, "aria-checked", String(on));
      }
    },
    lock: (disabled) => {
      for (const row of rows) {
        setDisabled(row, disabled);
      }
    },
    show: (value, shown) => {
      const row = byValue.get(value);
      if (row) {
        setHidden(row, !shown);
      }
    },
  };
};
