/* Small stateless pieces shared by the pages. Called as functions where the caller keeps the element. */
import type { LabelKey } from '../i18n';
import { Icon, type IconName, Spinner } from '../icons';

/* Info note; `warning` = the warning variant. */
export const Note = (p: { label: LabelKey; id?: string; warning?: boolean }): HTMLParagraphElement => {
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

/* "Connect your AirPods to change these." once at the top of a group whose controls are locked. */
export const LockRow = (id: string): HTMLDivElement => (
  <div class="wp-row">{Note({ label: 'common.connectToChange', id })}</div>
) as HTMLDivElement;

/* Spinner + "Checking..." in place of a control while the initial report is pending. */
export const Checking = (): HTMLSpanElement => (
  <span class="wp-row__end wp-row__checking" hidden>
    <Spinner />
    <span l:text="common.checking" />
  </span>
) as HTMLSpanElement;

type Switch = { el: HTMLLabelElement; input: HTMLInputElement };
export const createSwitch = (labelledBy: string, onChange: (on: boolean) => void): Switch => {
  let input!: HTMLInputElement;
  const el = (
    <label class="wp-switch">
      <input class="wp-switch__input" type="checkbox" role="switch" aria-labelledby={labelledBy} ref={input}
        onchange={() => onChange(input.checked)} />
      <span class="wp-switch__track"><span class="wp-switch__thumb" /></span>
    </label>
  ) as HTMLLabelElement;
  return { el, input };
};

const Step = (p: { n: string; title: LabelKey; desc: LabelKey }) => (
  <li>
    <span class="wp-steps__icon t-body-strong">{p.n}</span>
    <span><span class="wp-steps__title" l:text={p.title} /><span class="wp-steps__desc" l:text={p.desc} /></span>
  </li>
);

export const PairSteps = () => (
  <ol class="wp-steps">
    <Step n="1" title="pair.step1Title" desc="pair.step1Desc" />
    <Step n="2" title="pair.step2Title" desc="pair.step2Desc" />
    <Step n="3" title="pair.step3Title" desc="pair.step3Desc" />
  </ol>
);

/* Navigation row to a sub-page. */
export const NavRow = (p: { icon: IconName; title: LabelKey; desc: LabelKey; onclick: () => void }): HTMLButtonElement => (
  <button type="button" class="wp-row wp-row--nav" onclick={p.onclick}>
    <span class="wp-row__lead"><Icon name={p.icon} /></span>
    <span class="wp-row__text"><span class="wp-row__title" l:text={p.title} /><span class="wp-row__desc" l:text={p.desc} /></span>
    <span class="wp-row__end"><Icon name="chevron-right" /></span>
  </button>
) as HTMLButtonElement;
