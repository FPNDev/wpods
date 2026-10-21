/* ChoiceList rows (.wp-row.wp-choice with a check): role="radio" for one of many, role="checkbox" for several.
   The caller puts the rows into a .wp-list carrying role="radiogroup" / "group". Rows are kept by value, so every
   update touches only the row that changes. `lock` = disabled (not connected); `block` = aria-disabled with a
   reason (the row stays focusable and announces why it can't change). Call `block` after `lock` in a render. */
import { setAttr, setDisabled, setHidden } from '../dom';
import { bindText, type LabelKey } from '../i18n';
import { Icon } from '../icons';

type ChoiceItem<V extends string> = { value: V; label: LabelKey; desc?: LabelKey };
type ChoiceList<V extends string> = {
  rows: HTMLButtonElement[];
  set: (value: V, on: boolean) => void;
  lock: (disabled: boolean, describedBy: string | null) => void;
  block: (value: V, blocked: boolean, describedBy: string | null) => void;
  show: (value: V, shown: boolean) => void;
};

export const createChoiceList = <V extends string>(o: {
  role: 'radio' | 'checkbox';
  items: ChoiceItem<V>[];
  onPick: (value: V) => void;
}): ChoiceList<V> => {
  const byValue = new Map<V, HTMLButtonElement>();
  const blocked = new Set<V>();
  const rows: HTMLButtonElement[] = [];
  for (const item of o.items) {
    const text = <span class="wp-row__text"><span class="wp-row__title">{bindText(item.label)}</span></span>;
    if (item.desc) {
      const desc = <span class="wp-row__desc">{bindText(item.desc)}</span>;
      text.append(desc);
    }
    const row = (
      <button type="button" class="wp-row wp-choice" role={o.role} aria-checked="false"
        onclick={() => {
          if (!blocked.has(item.value)) {
            o.onPick(item.value);
          }
        }}>
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
        setAttr(row, 'aria-checked', String(on));
      }
    },
    lock: (disabled, describedBy) => {
      for (const row of rows) {
        setDisabled(row, disabled);
        setAttr(row, 'aria-describedby', disabled ? describedBy : null);
      }
    },
    block: (value, on, describedBy) => {
      const row = byValue.get(value);
      if (!row) {
        return;
      }
      if (on) {
        blocked.add(value);
      } else {
        blocked.delete(value);
      }
      setAttr(row, 'aria-disabled', on ? 'true' : null);
      setAttr(row, 'aria-describedby', on ? describedBy : null);
    },
    show: (value, shown) => {
      const row = byValue.get(value);
      if (row) {
        setHidden(row, !shown);
      }
    },
  };
};
