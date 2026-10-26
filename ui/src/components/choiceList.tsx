import { setAttr, setDisabled, setHidden } from "../dom";
import { bindText, type LabelKey } from "../i18n";
import { Icon } from "../icons";

type ChoiceItem<V extends string> = {
  value: V;
  label: LabelKey;
  desc?: LabelKey;
};
type ChoiceList<V extends string> = {
  rows: HTMLButtonElement[];
  set: (value: V, on: boolean) => void;
  lock: (disabled: boolean, describedBy: string | null) => void;
  show: (value: V, shown: boolean) => void;
};

export const createChoiceList = <V extends string>(o: {
  role: "radio" | "checkbox";
  items: ChoiceItem<V>[];
  onPick: (value: V) => void;
}): ChoiceList<V> => {
  const byValue = new Map<V, HTMLButtonElement>();
  const rows: HTMLButtonElement[] = [];
  for (const item of o.items) {
    const text = (
      <span class="wp-row__text">
        <span class="wp-row__title">{bindText(item.label)}</span>
      </span>
    );
    if (item.desc) {
      const desc = <span class="wp-row__desc">{bindText(item.desc)}</span>;
      text.append(desc);
    }
    const row = (
      <button
        type="button"
        class="wp-row wp-choice"
        role={o.role}
        aria-checked="false"
        onclick={() => o.onPick(item.value)}
      >
        {text}
        <Icon name="check" class="wp-row__check" />
      </button>
    ) as HTMLButtonElement;
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
    lock: (disabled, describedBy) => {
      for (const row of rows) {
        setDisabled(row, disabled);
        setAttr(row, "aria-describedby", disabled ? describedBy : null);
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
