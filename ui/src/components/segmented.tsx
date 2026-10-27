/* Segmented control: a radio group in a fieldset with a hidden legend. Keeps its inputs by value and the checked
   value, so every update touches only the inputs that change. */
import { setAttr, setDisabled, setHidden } from '../dom';
import { bindText, type LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';

type SegItem<V extends string> = { value: V; label: LabelKey; icon?: IconName };
export type Segmented<V extends string> = {
  el: HTMLFieldSetElement;
  set: (value: V | null) => void;
  lockItem: (value: V, disabled: boolean, describedBy: string | null) => void;
  lock: (disabled: boolean) => void;
  showItem: (value: V, shown: boolean) => void;
};

export const createSegmented = <V extends string>(o: {
  name: string;
  legend: LabelKey;
  items: SegItem<V>[];
  icons?: boolean;
  onChange: (value: V) => void;
}): Segmented<V> => {
  const inputs = new Map<V, HTMLInputElement>();
  const labels = new Map<V, HTMLLabelElement>();
  let current: V | null = null;
  const el = (
    <fieldset class={o.icons ? 'wp-seg wp-seg--icons' : 'wp-seg'}>
      <legend class="wp-sr" l:text={o.legend} />
    </fieldset>
  ) as HTMLFieldSetElement;
  for (const item of o.items) {
    let input!: HTMLInputElement;
    const face = (
      <span class="wp-seg__face">
        <span class="wp-seg__label">{bindText(item.label)}</span>
      </span>
    );
    if (item.icon) {
      const disc = (
        <span class="wp-seg__disc">
          <Icon name={item.icon} class="wp-icon--lg" />
        </span>
      );
      face.prepend(disc);
    }
    const label = (
      <label class="wp-seg__item">
        <input
          class="wp-seg__input"
          type="radio"
          name={o.name}
          value={item.value}
          ref={input}
          onchange={() => {
            current = item.value;
            o.onChange(item.value);
          }}
        />
        {face}
      </label>
    ) as HTMLLabelElement;
    inputs.set(item.value, input);
    labels.set(item.value, label);
    el.append(label);
  }

  const lockItem = (
    value: V,
    disabled: boolean,
    describedBy: string | null,
  ): void => {
    const input = inputs.get(value);
    if (input) {
      setDisabled(input, disabled);
      setAttr(input, 'aria-describedby', describedBy);
    }
  };
  return {
    el,
    set: (value) => {
      if (value === current) {
        return;
      }
      if (current !== null) {
        inputs.get(current)!.checked = false;
      }
      current = value;
      if (value !== null) {
        inputs.get(value)!.checked = true;
      }
    },
    lockItem,
    lock: (disabled) => {
      for (const value of inputs.keys()) {
        lockItem(value, disabled, null);
      }
    },
    showItem: (value, shown) => {
      const label = labels.get(value);
      if (label) {
        setHidden(label, !shown);
      }
    },
  };
};
