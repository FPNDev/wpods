import type { AccentRamp, Theme } from "./protocol";

type Rgb = [number, number, number];
type ThemeTokens = { card: string; onAccent: string };

const root = document.documentElement;
const darkQuery = matchMedia("(prefers-color-scheme: dark)");
const ACCENT_VARS = [
  "--accent",
  "--accent-hover",
  "--accent-pressed",
  "--on-accent",
  "--accent-text",
  "--focus-ring",
];
const WEIGHTS = [0.2126, 0.7152, 0.0722];

let themePref: Theme = "auto";
let useAccent = false;
let ramp: AccentRamp | null = null;
let tokens: Record<"light" | "dark", ThemeTokens> | null = null;

const readTokens = (): Record<"light" | "dark", ThemeTokens> => {
  const read = (theme: "light" | "dark"): ThemeTokens => {
    const probe = document.createElement("div");
    probe.dataset.theme = theme;
    probe.hidden = true;
    document.body.append(probe);
    const cs = getComputedStyle(probe);
    const out = {
      card: cs.getPropertyValue("--surface-card").trim(),
      onAccent: cs.getPropertyValue("--on-accent").trim(),
    };
    probe.remove();
    return out;
  };
  return { light: read("light"), dark: read("dark") };
};

const rgb = (hex: string): Rgb | null => {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m) {
    return null;
  }
  const n = parseInt(m[1], 16);
  return [n >> 16, (n >> 8) & 255, n & 255];
};
const luminance = (hex: string): number => {
  let sum = 0;
  for (const [i, v] of (rgb(hex) ?? [0, 0, 0]).entries()) {
    const s = v / 255;
    sum +=
      WEIGHTS[i] * (s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4);
  }
  return sum;
};
const contrast = (a: string, b: string): number => {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
};

const pickAccent = (
  r: AccentRamp,
  t: Record<"light" | "dark", ThemeTokens>,
  theme: "light" | "dark",
): string[] | null => {
  const steps = [
    r.dark3,
    r.dark2,
    r.dark1,
    r.accent,
    r.light1,
    r.light2,
    r.light3,
  ];
  for (const s of steps) {
    if (!rgb(s)) {
      return null;
    }
  }
  const dark = theme === "dark";
  const at = dark ? 5 : 2;
  const accent = steps[at];
  let on: string | null = null;
  let best = 4.5;
  for (const ink of [t.light.onAccent, t.dark.onAccent]) {
    const c = contrast(ink, accent);
    if (c >= best) {
      best = c;
      on = ink;
    }
  }
  if (!on) {
    return null;
  }
  const card = t[theme].card;
  for (let i = at; i >= 0 && i < steps.length; i += dark ? 1 : -1) {
    if (contrast(steps[i], card) >= 4.5) {
      return [
        accent,
        steps[dark ? 6 : 3],
        steps[dark ? 4 : 1],
        on,
        steps[i],
        steps[i],
      ];
    }
  }
  return null;
};

const apply = (): void => {
  const theme =
    themePref === "auto" ? (darkQuery.matches ? "dark" : "light") : themePref;
  if (root.dataset.theme !== theme) {
    root.dataset.theme = theme;
  }
  tokens ??= readTokens();
  const values = useAccent && ramp ? pickAccent(ramp, tokens, theme) : null;
  for (const [i, name] of ACCENT_VARS.entries()) {
    if (values) {
      root.style.setProperty(name, values[i]);
    } else {
      root.style.removeProperty(name);
    }
  }
};
darkQuery.addEventListener("change", () => {
  if (themePref === "auto") {
    apply();
  }
});

export const getTheme = (): Theme => themePref;
export const getUseAccent = (): boolean => useAccent;

export const setThemePrefs = (p: {
  theme?: Theme;
  useAccent?: boolean;
  accent?: AccentRamp | null;
}): void => {
  if (p.theme !== undefined) {
    themePref = p.theme;
  }
  if (p.useAccent !== undefined) {
    useAccent = p.useAccent;
  }
  if (p.accent !== undefined) {
    ramp = p.accent;
  }
  apply();
};
