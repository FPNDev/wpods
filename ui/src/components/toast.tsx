/* Toast: one at a time, closes itself after --dur-toast (paused while hovered or focused), x closes it at once.
   Only its buttons take the pointer (app.css), so it never blocks the controls under it. */
import { setHidden, setText } from "../dom";
import { t } from "../i18n";
import { Icon } from "../icons";
import type { ToastId } from "../protocol";

type ToastAction = "reconnect" | "tryAgain" | "openSettings";
type Toast = {
  el: HTMLDivElement;
  show: (id: ToastId, code: string | null) => void;
  hide: () => void;
  relabel: () => void;
};

const ACTIONS: Record<ToastId, ToastAction | null> = {
  noResponse: "reconnect",
  connectFailed: "tryAgain",
  bluetoothOff: "openSettings",
  micFailed: null,
  lowLatencyFailed: null,
  handsFreeFailed: null,
  disconnectFailed: null,
  micModelFailed: null,
  micModeStopped: null,
  settingsReset: null,
};

export const createToast = (onAction: (action: ToastAction) => void): Toast => {
  let msg!: HTMLSpanElement;
  let actionBtn!: HTMLButtonElement;
  let shown: ToastId | null = null;
  let code: string | null = null;
  let timer = 0;
  let ms = 0;
  const hide = (): void => {
    clearTimeout(timer);
    shown = null;
    setHidden(el, true);
  };
  const pause = (): void => clearTimeout(timer);
  const arm = (): void => {
    clearTimeout(timer);
    if (shown) {
      timer = setTimeout(hide, ms);
    }
  };
  const el = (
    <div
      class="wp-toast wp-toast--error"
      role="alert"
      hidden
      onpointerenter={pause}
      onpointerleave={arm}
      onfocusin={pause}
      onfocusout={arm}
      onkeydown={(e) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          hide();
        }
      }}
    >
      <Icon name="error" class="wp-toast__icon wp-icon--lg" />
      <span class="wp-toast__msg" ref={msg} />
      <button
        type="button"
        class="wp-btn wp-btn--subtle"
        ref={actionBtn}
        onclick={() => {
          const action = shown && ACTIONS[shown];
          hide();
          if (action) {
            onAction(action);
          }
        }}
      />
      <button
        type="button"
        class="wp-iconbtn"
        l:aria-label="common.dismiss"
        onclick={hide}
      >
        <Icon name="close" />
      </button>
    </div>
  ) as HTMLDivElement;

  const relabel = (): void => {
    if (!shown) {
      return;
    }
    const action = ACTIONS[shown];
    setText(msg, t(`toast.${shown}`, code === null ? undefined : { code }));
    setHidden(actionBtn, !action);
    if (action) {
      setText(actionBtn, t(`toast.action.${action}`));
    }
  };
  return {
    el,
    show: (id, faultCode) => {
      if (!ms) {
        ms =
          parseFloat(getComputedStyle(el).getPropertyValue("--dur-toast")) ||
          4000;
      }
      shown = id;
      code = faultCode;
      relabel();
      setHidden(el, false);
      arm();
    },
    hide,
    relabel,
  };
};
