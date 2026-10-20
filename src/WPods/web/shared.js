/* WPods page runtime shared by index.html and popup.html: host messaging, labels, theme and accent,
   DOM helpers and battery rings. Classic script (no modules); exposes top-level consts to the page script. */
'use strict';

const webview = window.chrome && window.chrome.webview;
const root = document.documentElement;
const tpl = document.createElement('template');

/* Host messaging */
const post = (msg) => {
  if (webview) {
    webview.postMessage(msg);
  }
};

/* Messages are applied to the page model as they arrive; the DOM is updated once per frame. */
const listen = (apply, render) => {
  let scheduled = false;
  const frame = () => {
    scheduled = false;
    render();
  };
  const schedule = () => {
    if (!scheduled) {
      scheduled = true;
      requestAnimationFrame(frame);
    }
  };
  if (webview) {
    webview.addEventListener('message', (e) => {
      apply(e.data);
      schedule();
    });
  }
  return schedule;
};

/* Labels */
let labels = {};
const t = (key, vars) => {
  const s = labels[key] ?? key;
  if (!vars) {
    return s;
  }
  return s.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));
};
const applyLabels = (scope) => {
  for (const el of scope.querySelectorAll('[data-l]')) {
    el.textContent = t(el.dataset.l);
  }
  for (const el of scope.querySelectorAll('[data-l-aria]')) {
    el.setAttribute('aria-label', t(el.dataset.lAria));
  }
  for (const el of scope.querySelectorAll('[data-l-ph]')) {
    el.placeholder = t(el.dataset.lPh);
  }
};

/* DOM helpers (all write only on change; none reads layout) */
const html = (markup) => {
  tpl.innerHTML = markup;
  return tpl.content.firstElementChild;
};
const collectRefs = (scope, refs) => {
  for (const el of scope.querySelectorAll('[data-ref]')) {
    refs[el.dataset.ref] = el;
  }
  return refs;
};
const setText = (el, s) => {
  if (el.textContent !== s) {
    el.textContent = s;
  }
};
const setHidden = (el, hidden) => {
  if (el.hidden !== hidden) {
    el.hidden = hidden;
  }
};
const setAttr = (el, name, value) => {
  if (value === null) {
    if (el.hasAttribute(name)) {
      el.removeAttribute(name);
    }
  } else if (el.getAttribute(name) !== value) {
    el.setAttribute(name, value);
  }
};
const setClass = (el, cls, on) => {
  if (el.classList.contains(cls) !== on) {
    el.classList.toggle(cls, on);
  }
};
const art = (model) => {
  const el = html(WPods.art(model));
  el.setAttribute('aria-label', t(`art.${model}`));
  return el;
};

/* Theme and Windows accent (guidelines/20) */
const darkQuery = matchMedia('(prefers-color-scheme: dark)');
const ACCENT_VARS = ['--accent', '--accent-hover', '--accent-pressed', '--on-accent', '--accent-text', '--focus-ring'];
let themePref = 'auto';
let useAccent = false;
let ramp = null;
let tokens = null;

/* Token values per theme, read once from probe elements (light and dark rules apply to them directly). */
const readTokens = () => {
  const out = {};
  for (const theme of ['light', 'dark']) {
    const probe = document.createElement('div');
    probe.dataset.theme = theme;
    probe.hidden = true;
    document.body.append(probe);
    const cs = getComputedStyle(probe);
    out[theme] = { card: cs.getPropertyValue('--surface-card').trim(), onAccent: cs.getPropertyValue('--on-accent').trim() };
    probe.remove();
  }
  return out;
};
const rgb = (hex) => {
  const m = /^#([0-9a-f]{6})$/i.exec(hex ?? '');
  if (!m) {
    return null;
  }
  const n = parseInt(m[1], 16);
  return [n >> 16, (n >> 8) & 255, n & 255];
};
const luminance = (c) => {
  let sum = 0;
  for (const [i, v] of c.entries()) {
    const s = v / 255;
    sum += [0.2126, 0.7152, 0.0722][i] * (s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4);
  }
  return sum;
};
const contrast = (a, b) => {
  const la = luminance(rgb(a));
  const lb = luminance(rgb(b));
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
};
/* Returns the six accent overrides for this theme, or null when the ramp can't pass 4.5:1 (default stays). */
const pickAccent = (theme) => {
  const steps = [ramp.dark3, ramp.dark2, ramp.dark1, ramp.accent, ramp.light1, ramp.light2, ramp.light3];
  for (const s of steps) {
    if (!rgb(s)) {
      return null;
    }
  }
  const dark = theme === 'dark';
  const at = dark ? 5 : 2;
  const accent = steps[at];
  let on = null;
  let best = 4.5;
  for (const ink of [tokens.light.onAccent, tokens.dark.onAccent]) {
    const c = contrast(ink, accent);
    if (c >= best) {
      best = c;
      on = ink;
    }
  }
  if (!on) {
    return null;
  }
  const card = tokens[theme].card;
  for (let i = at; i >= 0 && i < steps.length; i += dark ? 1 : -1) {
    if (contrast(steps[i], card) >= 4.5) {
      return [accent, steps[dark ? 6 : 3], steps[dark ? 4 : 1], on, steps[i], steps[i]];
    }
  }
  return null;
};
const applyTheme = () => {
  const theme = themePref === 'auto' ? (darkQuery.matches ? 'dark' : 'light') : themePref;
  if (root.dataset.theme !== theme) {
    root.dataset.theme = theme;
  }
  if (!tokens) {
    tokens = readTokens();
  }
  const values = useAccent && ramp ? pickAccent(theme) : null;
  for (const [i, name] of ACCENT_VARS.entries()) {
    if (values) {
      root.style.setProperty(name, values[i]);
    } else {
      root.style.removeProperty(name);
    }
  }
};
darkQuery.addEventListener('change', () => {
  if (themePref === 'auto') {
    applyTheme();
  }
});

/* Fields both pages take from init, prefs, accent and visibility; the page script handles the rest. */
const applyCommon = (msg) => {
  switch (msg.type) {
    case 'init':
      labels = msg.labels ?? {};
      root.lang = msg.lang;
      root.dir = msg.rtl ? 'rtl' : 'ltr';
      themePref = msg.theme;
      useAccent = msg.useAccent;
      ramp = msg.accent;
      applyTheme();
      document.body.classList.toggle('is-hidden', msg.visible === false);
      break;
    case 'prefs':
      if (msg.theme !== undefined) {
        themePref = msg.theme;
      }
      if (msg.useAccent !== undefined) {
        useAccent = msg.useAccent;
      }
      applyTheme();
      break;
    case 'accent':
      ramp = msg.accent;
      applyTheme();
      break;
    case 'visibility':
      document.body.classList.toggle('is-hidden', !msg.visible);
      break;
    default:
      break;
  }
};

/* Battery rings (BatteryRing component), built once, updated in place */
const RING_C = 2 * Math.PI * 23;
const RING_GLYPH = { left: 'bud-left', right: 'bud-right', max: 'max' };
const createRing = (part, compact) => {
  const el = html(`<div class="wp-battery" role="img" hidden><div class="wp-ring"><svg viewBox="0 0 52 52" aria-hidden="true">` +
    `<circle class="wp-ring__track" cx="26" cy="26" r="23"/><circle class="wp-ring__fill" cx="26" cy="26" r="23"/></svg>` +
    `<div class="wp-ring__glyph">${WPods.glyph(RING_GLYPH[part] ?? 'case-pro')}</div><div class="wp-ring__bolt" hidden>${WPods.icon('bolt')}</div></div>` +
    `<div class="wp-battery__pct" aria-hidden="true"></div>` +
    (compact ? '' : '<div class="wp-battery__label" aria-hidden="true"></div><div class="wp-battery__ear" aria-hidden="true" hidden></div>') +
    '</div>');
  return {
    part,
    el,
    fill: el.querySelector('.wp-ring__fill'),
    glyph: el.querySelector('.wp-ring__glyph'),
    bolt: el.querySelector('.wp-ring__bolt'),
    pct: el.querySelector('.wp-battery__pct'),
    label: el.querySelector('.wp-battery__label'),
    ear: el.querySelector('.wp-battery__ear'),
    caseGlyph: 'case-pro',
    key: '',
  };
};
/* bat: { pct, charging } or null (absent: not drawn). ear: localized ear text or '' (buds only). */
const updateRing = (r, bat, ear, caseGlyph) => {
  setHidden(r.el, !bat);
  if (!bat) {
    return;
  }
  if (r.part === 'case' && caseGlyph !== r.caseGlyph) {
    r.caseGlyph = caseGlyph;
    r.glyph.innerHTML = WPods.glyph(caseGlyph);
  }
  const key = `${bat.pct}|${bat.charging}|${ear}`;
  if (key === r.key) {
    return;
  }
  r.key = key;
  const level = bat.charging ? 'charging' : bat.pct < 10 ? 'low' : bat.pct < 30 ? 'warn' : 'ok';
  r.el.className = `wp-battery wp-battery--${level}`;
  r.fill.setAttribute('stroke-dasharray', `${(RING_C * Math.max(0, Math.min(100, bat.pct)) / 100).toFixed(2)} ${RING_C.toFixed(2)}`);
  setHidden(r.bolt, !bat.charging);
  const name = t(`battery.${r.part}`);
  setText(r.pct, t('battery.pct', { pct: bat.pct }));
  if (r.label) {
    /* The case's second line says it charges; buds show where they are. */
    const line = r.part === 'case' ? (bat.charging ? t('battery.charging') : '') : ear;
    setText(r.label, name);
    setText(r.ear, line);
    setHidden(r.ear, !line);
  }
  let sr = t('battery.sr', { part: name, pct: bat.pct });
  if (bat.charging) {
    sr += `, ${t('battery.charging').toLowerCase()}`;
  }
  if (ear) {
    sr += `, ${ear.toLowerCase()}`;
  }
  r.el.setAttribute('aria-label', sr);
};
