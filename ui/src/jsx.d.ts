import type { LabelKey } from './i18n';

type Handlers<M> = { [K in keyof M as `on${K & string}`]?: (e: M[K]) => void };

type LabelAttrs = {
  'l:text'?: LabelKey;
  'l:aria-label'?: LabelKey;
  'l:placeholder'?: LabelKey;
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
    hidden?: true;
    [aria: `aria-${string}`]: string;
  };

type SvgAttrs<E> = {
  ref?: E;
  class?: string;
  viewBox?: string;
  'aria-hidden'?: string;
  cx?: string;
  cy?: string;
  r?: string;
  rx?: string;
  ry?: string;
  x?: string;
  y?: string;
  x1?: string;
  y1?: string;
  x2?: string;
  y2?: string;
  width?: string;
  height?: string;
  d?: string;
  points?: string;
  transform?: string;
  'stroke-dasharray'?: string;
};

type SvgTag =
  | 'svg'
  | 'g'
  | 'path'
  | 'circle'
  | 'ellipse'
  | 'rect'
  | 'line'
  | 'polyline'
  | 'polygon';

declare global {
  namespace JSX {
    type Element = globalThis.Element;
    type IntrinsicElements = {
      [K in keyof HTMLElementTagNameMap]: HtmlAttrs<HTMLElementTagNameMap[K]>;
    } & { [K in SvgTag]: SvgAttrs<SVGElementTagNameMap[K]> };
  }
}
