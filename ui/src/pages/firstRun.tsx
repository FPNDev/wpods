/* First run: welcome, three facts (the last carries the startup switch), the not-an-Apple-app disclaimer with a link
   to the full terms, Agree and get started (= accepting it). */
import { createSwitch } from '../components/common';
import { setChecked } from '../dom';
import { post } from '../host';
import type { LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';
import { store } from '../state';
import type { Ctx, Page } from './page';

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
          <Fact icon="mic" title="firstRun.micTitle" desc="firstRun.micDesc" />
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
            onclick={() => ctx.go('legal')}
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
    sections: { firstRun: () => setChecked(startup.input, store.startup) },
    returnFocus: () => readTerms,
  };
};
