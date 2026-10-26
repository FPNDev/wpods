import type en from "../../src/WPods/locales/en-US.json";

export type LabelKey = keyof (typeof en)["labels"];
type Vars = Record<string, string | number>;
type TextBinding = { node: Text; key: LabelKey };
type AttrBinding = { el: Element; name: string; key: LabelKey };

const root = document.documentElement;
let labels: Record<string, string> = {};
const texts: TextBinding[] = [];
const attrs: AttrBinding[] = [];

export let labelsVersion = 0;

export const t = (key: LabelKey, vars?: Vars): string => {
  const s = labels[key] ?? key;
  if (!vars) {
    return s;
  }
  return s.replace(/\{(\w+)\}/g, (m, name: string) =>
    name in vars ? String(vars[name]) : m,
  );
};

export const bindText = (key: LabelKey): Text => {
  const node = document.createTextNode(t(key));
  texts.push({ node, key });
  return node;
};

export const bindAttr = (el: Element, name: string, key: LabelKey): void => {
  el.setAttribute(name, t(key));
  attrs.push({ el, name, key });
};

export const setLabels = (
  next: Record<string, string>,
  lang: string,
  rtl: boolean,
): void => {
  labels = next;
  labelsVersion++;
  root.lang = lang;
  root.dir = rtl ? "rtl" : "ltr";
  for (const b of texts) {
    b.node.data = t(b.key);
  }
  for (const b of attrs) {
    b.el.setAttribute(b.name, t(b.key));
  }
};
