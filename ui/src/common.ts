import { setLabels } from './i18n';
import type { HostToMain, HostToOsd, HostToPopup } from './protocol';
import { setThemePrefs } from './theme';

const setVisible = (visible: boolean): void => {
  document.body.classList.toggle('is-hidden', !visible);
};

/** `relabel` receives a "labels" message to re-render once the language's hyphenation patterns have loaded. */
export const applyCommon = <T extends HostToMain | HostToPopup | HostToOsd>(
  msg: T,
  relabel: (msg: T) => void,
): void => {
  const again = (): void => {
    if (msg.type === 'init' || msg.type === 'labels') {
      relabel({
        type: 'labels',
        labels: msg.labels,
        lang: msg.lang,
        rtl: msg.rtl,
      } as T);
    }
  };
  switch (msg.type) {
    case 'init':
      setLabels(msg.labels, msg.lang, msg.rtl, again);
      setThemePrefs({
        theme: msg.theme,
        useAccent: msg.useAccent,
        accent: msg.accent,
      });
      setVisible(msg.visible);
      break;
    case 'labels':
      setLabels(msg.labels, msg.lang, msg.rtl, again);
      break;
    case 'prefs':
      setThemePrefs({ theme: msg.theme, useAccent: msg.useAccent });
      break;
    case 'accent':
      setThemePrefs({ accent: msg.accent });
      break;
    case 'visibility':
      setVisible(msg.visible);
      break;
    default:
      break;
  }
};
