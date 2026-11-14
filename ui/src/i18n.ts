import type en from '../../src/WPods/locales/en-US.json';

export type LabelKey = keyof (typeof en)['labels'];
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

  return s.replaceAll(/\{(\w+)\}/gu, (m, name: string) =>
    name in vars ? String(vars[name]) : m,
  );
};

/* WebView2 has no hyphenation dictionaries, so CSS `hyphens: auto` does nothing; shown text gets soft hyphens from
   the "hyphen" library instead. Languages without patterns here are shown unhyphenated. */
type HyphenModule = { default: { hyphenateSync: (text: string) => string } };
const PATTERNS: Record<string, () => Promise<HyphenModule>> = {
  bg: () => import('hyphen/bg'),
  bn: () => import('hyphen/bn'),
  cs: () => import('hyphen/cs'),
  da: () => import('hyphen/da'),
  de: () => import('hyphen/de-1996'),
  el: () => import('hyphen/el-monoton'),
  'en-US': () => import('hyphen/en-us'),
  en: () => import('hyphen/en-gb'),
  es: () => import('hyphen/es'),
  fi: () => import('hyphen/fi'),
  fr: () => import('hyphen/fr'),
  gu: () => import('hyphen/gu'),
  hi: () => import('hyphen/hi'),
  hr: () => import('hyphen/hr'),
  hu: () => import('hyphen/hu'),
  it: () => import('hyphen/it'),
  kn: () => import('hyphen/kn'),
  lt: () => import('hyphen/lt'),
  ml: () => import('hyphen/ml'),
  mr: () => import('hyphen/mr'),
  nb: () => import('hyphen/nb'),
  nl: () => import('hyphen/nl'),
  or: () => import('hyphen/or'),
  pa: () => import('hyphen/pa'),
  pl: () => import('hyphen/pl'),
  pt: () => import('hyphen/pt'),
  sk: () => import('hyphen/sk'),
  sl: () => import('hyphen/sl'),
  ta: () => import('hyphen/ta'),
  te: () => import('hyphen/te'),
  uk: () => import('hyphen/uk'),
};
const plain = (s: string): string => s;
const hyphenators = new Map<string, (s: string) => string>();
let hyphenate = plain;

/** Adds soft hyphens for the current language once its patterns are loaded. */
export const shown = (s: string): string => hyphenate(s);

export const bindText = (key: LabelKey): Text => {
  const node = document.createTextNode(shown(t(key)));
  texts.push({ node, key });

  return node;
};

export const bindAttr = (el: Element, name: string, key: LabelKey): void => {
  el.setAttribute(name, t(key));
  attrs.push({ el, name, key });
};

/** Applies a language; `onHyphenation` fires once its patterns have loaded, if the language is still current. */
export const setLabels = (
  next: Record<string, string>,
  lang: string,
  rtl: boolean,
  onHyphenation: () => void,
): void => {
  labels = next;
  labelsVersion++;
  root.lang = lang;
  root.dir = rtl ? 'rtl' : 'ltr';
  const ready = hyphenators.get(lang);
  hyphenate = ready ?? plain;
  for (const b of texts) {
    b.node.data = shown(t(b.key));
  }
  for (const b of attrs) {
    b.el.setAttribute(b.name, t(b.key));
  }
  const load = PATTERNS[lang] ?? PATTERNS[lang.split('-')[0]];
  if (ready || !load) {
    return;
  }
  void load().then((m) => {
    hyphenators.set(lang, m.default.hyphenateSync);
    if (root.lang === lang) {
      onHyphenation();
    }
  });
};
