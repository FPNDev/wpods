/* WebView2 bridge: page -> host via postMessage, host -> page via the `message` event. Outside WebView2 both are no-ops. */
import type { MainToHost, PopupToHost } from './protocol';

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void;
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

/* Host messages arrive already parsed (PostWebMessageAsJson); `M` is the page's incoming union. */
export const listen = <M>(apply: (message: M) => void): void => {
  webview?.addEventListener('message', (e) => apply(e.data as M));
};
