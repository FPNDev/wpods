import { setLabels } from "./i18n";
import type { HostToMain, HostToPopup } from "./protocol";
import { setThemePrefs } from "./theme";

const setVisible = (visible: boolean): void => {
  document.body.classList.toggle("is-hidden", !visible);
};

export const applyCommon = (msg: HostToMain | HostToPopup): void => {
  switch (msg.type) {
    case "init":
      setLabels(msg.labels, msg.lang, msg.rtl);
      setThemePrefs({
        theme: msg.theme,
        useAccent: msg.useAccent,
        accent: msg.accent,
      });
      setVisible(msg.visible);
      break;
    case "labels":
      setLabels(msg.labels, msg.lang, msg.rtl);
      break;
    case "prefs":
      setThemePrefs({ theme: msg.theme, useAccent: msg.useAccent });
      break;
    case "accent":
      setThemePrefs({ accent: msg.accent });
      break;
    case "visibility":
      setVisible(msg.visible);
      break;
    default:
      break;
  }
};
