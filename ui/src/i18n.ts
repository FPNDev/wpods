/* Labels from the host (`init` / `labels`). Static texts and attributes are bound once by key (JSX `l:` attributes)
   and rewritten in place when the language changes: no rebuild, no reload. Dynamic texts are set by the renders,
   which the pages re-run after a language change. */
import type en from '../../src/WPods/locales/en-US.json';

export type LabelKey = keyof (typeof en)['labels'];
type Vars = Record<string, string | number>;
type TextBinding = { node: Text; key: LabelKey };
type AttrBinding = { el: Element; name: string; key: LabelKey };

const root = document.documentElement;
let labels: Record<string, string> = {};
const texts: TextBinding[] = [];
const attrs: AttrBinding[] = [];

/* Bumped on every language change, so renders that cache composed texts know to redo them. */
export let labelsVersion = 0;

export const t = (key: LabelKey, vars?: Vars): string => {
  const s = labels[key] ?? key;
  if (!vars) {
    return s;
  }
  return s.replace(/\{(\w+)\}/g, (m, name: string) => (name in vars ? String(vars[name]) : m));
};

/* Only for nodes that live as long as the page (bindings are never released). */
export const bindText = (key: LabelKey): Text => {
  const node = document.createTextNode(t(key));
  texts.push({ node, key });
  return node;
};

export const bindAttr = (el: Element, name: string, key: LabelKey): void => {
  el.setAttribute(name, t(key));
  attrs.push({ el, name, key });
};

export const setLabels = (next: Record<string, string>, lang: string, rtl: boolean): void => {
  labels = next;
  labelsVersion++;
  root.lang = lang;
  root.dir = rtl ? 'rtl' : 'ltr';
  for (const b of texts) {
    b.node.data = t(b.key);
  }
  for (const b of attrs) {
    b.el.setAttribute(b.name, t(b.key));
  }
};
