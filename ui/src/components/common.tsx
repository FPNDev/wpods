import { setAttr, setClass } from '../dom';
import type { LabelKey } from '../i18n';
import { Icon, type IconName, Spinner } from '../icons';

export const Note = (p: {
  label: LabelKey;
  id?: string;
  warning?: boolean;
}): HTMLParagraphElement => {
  const el = (
    <p class={p.warning ? 'wp-note wp-note--warning' : 'wp-note'}>
      <Icon name={p.warning ? 'warning' : 'info'} />
      <span l:text={p.label} />
    </p>
  ) as HTMLParagraphElement;
  if (p.id) {
    el.id = p.id;
  }
  return el;
};

const VALUE_KEYS = new Set([
  ' ',
  'ArrowLeft',
  'ArrowRight',
  'ArrowUp',
  'ArrowDown',
  'Home',
  'End',
  'PageUp',
  'PageDown',
]);

export type Busy = {
  spin: HTMLSpanElement;
  set: (spin: boolean, block: boolean) => void;
};
export const createBusy = (
  control: HTMLElement,
  items: Iterable<Element>,
): Busy => {
  let blocked = false;
  const stop = (e: Event): void => {
    if (blocked) {
      e.preventDefault();
    }
  };
  control.addEventListener('click', stop, true);
  control.addEventListener('mousedown', stop, true);
  control.addEventListener(
    'keydown',
    (e) => {
      if (VALUE_KEYS.has(e.key)) {
        stop(e);
      }
    },
    true,
  );
  const spin = (
    <span class="wp-row__end wp-busy" role="img" l:aria-label="common.checking">
      <Spinner class="wp-spinner--sm" />
    </span>
  ) as HTMLSpanElement;
  return {
    spin,
    set: (spinning, block) => {
      blocked = block;
      setClass(spin, 'is-busy', spinning);
      setAttr(control, 'aria-busy', spinning ? 'true' : null);
      for (const item of items) {
        setAttr(item, 'aria-disabled', block ? 'true' : null);
      }
    },
  };
};

type Switch = { el: HTMLLabelElement; input: HTMLInputElement };
export const createSwitch = (
  labelledBy: string,
  onChange: (on: boolean) => void,
): Switch => {
  let input!: HTMLInputElement;
  const el = (
    <label class="wp-switch">
      <input
        class="wp-switch__input"
        type="checkbox"
        role="switch"
        aria-labelledby={labelledBy}
        ref={input}
        onchange={() => onChange(input.checked)}
      />
      <span class="wp-switch__track">
        <span class="wp-switch__thumb" />
      </span>
    </label>
  ) as HTMLLabelElement;
  return { el, input };
};

const Step = (p: { n: string; title: LabelKey; desc: LabelKey }) => (
  <li>
    <span class="wp-steps__icon t-body-strong">{p.n}</span>
    <span>
      <span class="wp-steps__title" l:text={p.title} />
      <span class="wp-steps__desc" l:text={p.desc} />
    </span>
  </li>
);

export const PairSteps = () => (
  <ol class="wp-steps">
    <Step n="1" title="pair.step1Title" desc="pair.step1Desc" />
    <Step n="2" title="pair.step2Title" desc="pair.step2Desc" />
    <Step n="3" title="pair.step3Title" desc="pair.step3Desc" />
  </ol>
);

export const NavRow = (p: {
  icon: IconName;
  title: LabelKey;
  desc: LabelKey;
  onclick: () => void;
}): HTMLButtonElement =>
  (
    <button type="button" class="wp-row wp-row--nav" onclick={p.onclick}>
      <span class="wp-row__lead">
        <Icon name={p.icon} />
      </span>
      <span class="wp-row__text">
        <span class="wp-row__title" l:text={p.title} />
        <span class="wp-row__desc" l:text={p.desc} />
      </span>
      <span class="wp-row__end">
        <Icon name="chevron-right" />
      </span>
    </button>
  ) as HTMLButtonElement;
