import type { MainToHost, PopupToHost } from "./protocol";

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(
    type: "message",
    listener: (e: { data: unknown }) => void,
  ): void;
};

declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}

const webview = window.chrome?.webview;

export const post = (message: MainToHost | PopupToHost): void => {
  webview?.postMessage(message);
};

export const listen = <M>(apply: (message: M) => void): void => {
  webview?.addEventListener("message", (e) => apply(e.data as M));
};
