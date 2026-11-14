// First-run page; "Agree and get started" accepts the not-an-Apple-app terms.
import { createSwitch } from '../components/common';
import { setChecked, setText } from '../dom';
import { post } from '../host';
import { t, type LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';
import { current, store } from '../state';
import type { Ctx, Page } from './page';

// The WPods mic shows up in Windows as "Headset (A2DP, <AirPods name>)": the connected pair, else the selected one.
const micName = (): string => {
  const d =
    store.attached === null
      ? current()
      : (store.byId.get(store.attached) ?? current());

  return d?.name ?? 'AirPods';
};

const Fact = (p: { icon: IconName; title: LabelKey; desc: LabelKey }) => (
  <li>
    <span class="wp-steps__icon">
      <Icon name={p.icon} />
    </span>
    <span>
      <span class="wp-steps__title" l:text={p.title} />
      <span class="wp-steps__desc" l:text={p.desc} />
    </span>
  </li>
);

export const createFirstRun = (ctx: Ctx): Page => {
  const startup = createSwitch('frStartupTitle', (value) => {
    store.startup = value;
    post({ type: 'setStartup', value });
  });
  let readTerms!: HTMLButtonElement;
  let micDesc!: HTMLSpanElement;
  const el = (
    <div class="wp-page">
      <div class="wp-empty">
        <h1
          class="wp-empty__title wp-empty__title--display"
          l:text="firstRun.title"
        />
        <p class="wp-empty__body" l:text="firstRun.body" />
        <ul class="wp-steps">
          <Fact icon="pc" title="firstRun.trayTitle" desc="firstRun.trayDesc" />
          <li>
            <span class="wp-steps__icon">
              <Icon name="mic" />
            </span>
            <span>
              <span class="wp-steps__title" l:text="firstRun.micTitle" />
              <span class="wp-steps__desc" ref={micDesc} />
            </span>
          </li>
          <li>
            <span class="wp-steps__icon">
              <Icon name="power" />
            </span>
            <span class="wp-steps__row">
              <span class="wp-steps__grow">
                <span
                  class="wp-steps__title"
                  id="frStartupTitle"
                  l:text="windows.startup"
                />
                <span class="wp-steps__desc" l:text="firstRun.startupDesc" />
              </span>
              {startup.el}
            </span>
          </li>
        </ul>
        <section class="wp-card wp-disclaimer" aria-labelledby="frLegalTitle">
          <h2
            class="wp-steps__title"
            id="frLegalTitle"
            l:text="legal.firstRunTitle"
          />
          <p class="wp-empty__body" l:text="legal.firstRunBody" />
          <button
            type="button"
            class="wp-btn wp-btn--subtle"
            l:text="legal.readTerms"
            ref={readTerms}
            onclick={() => {
              ctx.go('legal');
            }}
          />
        </section>
        <div class="wp-empty__actions">
          <button
            type="button"
            class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
            l:text="firstRun.agree"
            onclick={() => {
              store.firstRun = false;
              post({ type: 'getStarted' });
              ctx.update('page');
            }}
          />
        </div>
      </div>
    </div>
  ) as HTMLDivElement;

  return {
    el,
    sections: {
      firstRun: () => {
        setChecked(startup.input, store.startup);
      },
      mic: () => {
        setText(micDesc, t('firstRun.micDesc', { name: micName() }));
      },
    },
    returnFocus: () => readTerms,
  };
};
