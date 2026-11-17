/* Shown instead of every AirPods page after a first install, since usbip-win2 works only after a Windows restart.
   "Restart now" asks for confirmation first because open work would be lost. */
import { setHidden } from '../dom';
import { post } from '../host';
import { Icon } from '../icons';
import type { Page } from './page';

export const createRestartNeeded = (): Page => {
  let body!: HTMLParagraphElement;
  let question!: HTMLParagraphElement;
  let actions!: HTMLDivElement;
  let confirm!: HTMLDivElement;
  let restart!: HTMLButtonElement;
  let confirmRestart!: HTMLButtonElement;
  const asking = (on: boolean): void => {
    setHidden(body, on);
    setHidden(actions, on);
    setHidden(question, !on);
    setHidden(confirm, !on);
    (on ? confirmRestart : restart).focus();
  };
  const el = (
    <div class="wp-page">
      <div class="wp-empty">
        <h1 class="wp-empty__title" l:text="restart.title" />
        <p class="wp-empty__body" l:text="restart.body" ref={body} />
        <p
          class="wp-empty__body"
          role="alert"
          hidden
          l:text="restart.confirm"
          ref={question}
        />
        <div class="wp-empty__actions" ref={actions}>
          <button
            type="button"
            class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
            ref={restart}
            onclick={() => {
              asking(true);
            }}
          >
            <Icon name="power" />
            <span l:text="restart.now" />
          </button>
          <button
            type="button"
            class="wp-btn wp-btn--subtle"
            l:text="restart.later"
            onclick={() => {
              post({ type: 'close' });
            }}
          />
        </div>
        <div class="wp-empty__actions" hidden ref={confirm}>
          <button
            type="button"
            class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
            l:text="restart.confirmRestart"
            ref={confirmRestart}
            onclick={() => {
              post({ type: 'restartWindows' });
            }}
          />
          <button
            type="button"
            class="wp-btn wp-btn--subtle"
            l:text="restart.cancel"
            onclick={() => {
              asking(false);
            }}
          />
        </div>
      </div>
    </div>
  ) as HTMLDivElement;

  return { el, sections: {} };
};
