import type { LabelKey } from "./i18n";

type Handlers<M> = { [K in keyof M as `on${K & string}`]?: (e: M[K]) => void };

type LabelAttrs = {
  "l:text"?: LabelKey;
  "l:aria-label"?: LabelKey;
  "l:placeholder"?: LabelKey;
};

type HtmlAttrs<E> = Handlers<HTMLElementEventMap> &
  LabelAttrs & {
    ref?: E;
    class?: string;
    id?: string;
    type?: string;
    role?: string;
    name?: string;
    value?: string;
    min?: string;
    max?: string;
    step?: string;
    tabindex?: string;
    lang?: string;
    hidden?: true;
    disabled?: true;
    [aria: `aria-${string}`]: string;
  };

type SvgAttrs<E> = {
  ref?: E;
  class?: string;
  viewBox?: string;
  role?: string;
  "aria-hidden"?: string;
  cx?: string;
  cy?: string;
  r?: string;
  "stroke-dasharray"?: string;
};

type SvgTag = "svg" | "circle";

declare global {
  namespace JSX {
    type Element = globalThis.Element;
    type IntrinsicElements = {
      [K in keyof HTMLElementTagNameMap]: HtmlAttrs<HTMLElementTagNameMap[K]>;
    } & { [K in SvgTag]: SvgAttrs<SVGElementTagNameMap[K]> };
  }
}
