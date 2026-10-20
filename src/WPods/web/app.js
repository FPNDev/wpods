/* WPods main window. Protocol: scratchpad ui-protocol.md. Model in `m`, DOM built once, updated in place
   per frame for the sections marked dirty. */
'use strict';

/* Static tables */
const PAGE_TITLE = { controls: 'controls.title', windows: 'windows.title', language: 'lang.title', pair: 'pair.title' };
const PARENT = { controls: 'home', windows: 'home', language: 'windows', pair: 'home' };
const SECTIONS = {
  firstRun: [],
  empty: [],
  pair: [],
  home: ['hero', 'nc', 'mic', 'features'],
  controls: ['controls'],
  windows: ['windows'],
  language: ['langs'],
};
const STATUS_KIND = { connected: 'connected', disconnected: 'disconnected', elsewhere: 'nearby', connecting: 'busy', reconnecting: 'busy' };
const EAR_KEY = { inEar: 'ear.inEar', outOfEar: 'ear.out', inCase: 'ear.inCase' };
const BATTERY_PARTS = { left: 'left', right: 'right', case: 'case', headphone: 'max' };
const TOASTS = {
  noResponse: { kind: 'error', action: 'reconnect' },
  connectFailed: { kind: 'error', action: 'tryAgain' },
  disconnected: { kind: 'error', action: 'reconnect' },
  bluetoothOff: { kind: 'error', action: 'openSettings' },
  micFailed: { kind: 'error', action: null },
  stillRunning: { kind: 'info', action: null },
  connected: { kind: 'success', action: null },
};
const FEATURES = [
  { key: 'earDetection', cap: null, title: 'features.ear', desc: 'features.earDesc', descMax: 'features.earDescMax' },
  { key: 'conversationAwareness', cap: 'conversationAwareness', title: 'features.conversation', desc: 'features.conversationDesc' },
  { key: 'oneBudAnc', cap: 'oneBudAnc', title: 'features.oneBud', desc: 'features.oneBudDesc' },
  { key: 'personalizedVolume', cap: 'personalizedVolume', title: 'features.personalizedVolume', desc: 'features.personalizedVolumeDesc' },
  { key: 'allowOff', cap: 'allowOff', title: 'features.allowOff', desc: 'features.allowOffDesc', descMax: 'features.allowOffDescMax' },
  { key: 'heartRate', cap: 'heartRate', title: 'features.heartRate', desc: 'features.heartRateDesc' },
  { key: 'hearingAid', cap: 'hearingAid', title: 'features.hearingAid', desc: 'features.hearingAidDesc' },
];
const HR_ON = { starting: true, streaming: true, noReading: true };

/* Model */
const m = {
  devices: [],
  byId: new Map(),
  attached: null,
  selected: null,
  state: null,
  mic: 'notActive',
  startup: false,
  firstRun: false,
  language: '',
  languages: [],
  windowsLanguageName: '',
  lastBattery: new Map(),
  lowSaid: new Set(),
};
const dirty = new Set();
let page = 'home';
let shown = null;
let navDir = 0;
let menuOpen = false;
let pendingToast = null;
let pendingAnnounce = '';
let toastTimer = 0;
let toastKind = '';
let toastDevice = null;
let toastAction = null;
let toastMs = 4000;
let lastTone = null;
let heroArtKey = '';
let langRows = [];
let ready = false;
const langByCode = new Map();
const mark = (...names) => {
  for (const n of names) {
    dirty.add(n);
  }
};

/* DOM: refs, pages, generated parts (built once) */
const refs = collectRefs(document, {});
const pages = {};
for (const el of document.querySelectorAll('[data-page]')) {
  pages[el.dataset.page] = el;
  el.remove();
}
refs.heroArt = refs.hero.querySelector('.wp-hero__art');
refs.status.prepend(html(WPods.spinner('wp-spinner--sm')));

const heroRings = { left: createRing('left'), right: createRing('right'), case: createRing('case'), max: createRing('max') };
for (const r of Object.values(heroRings)) {
  refs.heroBatteries.append(r.el);
}

const features = {};
for (const f of FEATURES) {
  const row = html(`<div class="wp-row wp-row--stack" hidden><div class="wp-row__head"><div class="wp-row__text">` +
    `<span class="wp-row__title" id="f-${f.key}" data-l="${f.title}"></span><span class="wp-row__desc" id="f-${f.key}-desc"></span></div>` +
    `<label class="wp-switch"><input class="wp-switch__input" type="checkbox" role="switch" aria-labelledby="f-${f.key}" data-feature="${f.key}">` +
    `<span class="wp-switch__track"><span class="wp-switch__thumb"></span></span></label>` +
    `<span class="wp-row__end wp-row__checking" hidden>${WPods.spinner()}<span data-l="common.checking"></span></span></div></div>`);
  features[f.key] = {
    def: f,
    row,
    desc: row.querySelector('.wp-row__desc'),
    sw: row.querySelector('.wp-switch'),
    input: row.querySelector('input'),
    check: row.querySelector('.wp-row__checking'),
  };
  refs.features.append(row);
}
const hrRow = features.heartRate.row;
refs.hrLive = html(`<div class="wp-hr" role="status" aria-live="off" hidden><span class="wp-hr__heart">${WPods.icon('heart', 'wp-icon--lg')}</span>` +
  `<span class="wp-hr__value"><span class="wp-hr__num"></span><span class="wp-hr__unit" data-l="hr.unit"></span></span></div>`);
refs.hrNum = refs.hrLive.querySelector('.wp-hr__num');
refs.hrWait = html(`<div class="wp-hr wp-hr--waiting" hidden><span class="wp-hr__heart">${WPods.icon('heart', 'wp-icon--lg')}</span><span class="wp-hr__wait" data-l="hr.waiting"></span></div>`);
refs.hrNote = html(`<p class="wp-note" hidden>${WPods.icon('info')}<span data-l="hr.note"></span></p>`);
hrRow.append(refs.hrLive, refs.hrWait, refs.hrNote);

const radios = (prefix, values) => {
  const out = {};
  for (const v of values) {
    out[v] = refs[`${prefix}_${v}`];
  }
  return out;
};
const ncRadios = radios('nc', ['off', 'noiseCancellation', 'transparency', 'adaptive']);
const micModeRadios = radios('mm', ['standard', 'voiceIsolation', 'wideSpectrum']);
const micSideRadios = radios('ms', ['automatic', 'left', 'right']);
const pressRadios = radios('ps', ['default', 'slower', 'slowest']);
const holdRadios = radios('hd', ['default', 'shorter', 'shortest']);
const themeRadios = radios('th', ['light', 'dark', 'auto']);

/* Render helpers */
const current = () => m.byId.get(m.selected) ?? null;
const checkRadio = (group, value) => {
  for (const [v, input] of Object.entries(group)) {
    const on = v === value;
    if (input.checked !== on) {
      input.checked = on;
    }
  }
};
const lockRadios = (group, disabled, describedBy) => {
  for (const input of Object.values(group)) {
    if (input.disabled !== disabled) {
      input.disabled = disabled;
    }
    setAttr(input, 'aria-describedby', describedBy);
  }
};
const setChecked = (input, on) => {
  if (input.checked !== on) {
    input.checked = on;
  }
};
const setDisabled = (input, disabled) => {
  if (input.disabled !== disabled) {
    input.disabled = disabled;
  }
};
const earText = (ear, left) => {
  if (!ear || ear.primaryLeft === null || ear.primaryLeft === undefined) {
    return '';
  }
  const status = ear.primaryLeft === left ? ear.primary : ear.secondary;
  return EAR_KEY[status] ? t(EAR_KEY[status]) : '';
};

/* Sections */
const renderPage = () => {
  const top = m.firstRun ? 'firstRun' : m.devices.length === 0 ? 'empty' : page;
  if (top === 'firstRun' || top === 'empty') {
    page = 'home';
  }
  if (top === shown) {
    return;
  }
  const left = shown;
  const el = pages[top];
  el.classList.remove('wp-page-enter', 'wp-page-back');
  if (left && navDir) {
    el.classList.add(navDir > 0 ? 'wp-page-enter' : 'wp-page-back');
  }
  refs.body.replaceChildren(el);
  refs.body.scrollTop = 0;
  shown = top;
  const sub = top in PAGE_TITLE;
  setHidden(refs.back, !sub);
  setHidden(refs.pageTitle, !sub);
  setHidden(refs.appTitle, sub);
  if (sub) {
    setText(refs.pageTitle, t(PAGE_TITLE[top]));
  }
  mark(...SECTIONS[top]);
  if (navDir > 0 && sub) {
    refs.back.focus();
  } else if (navDir < 0) {
    const from = { controls: refs.toControls, windows: refs.toWindows, language: refs.toLanguage, pair: refs.devSwitch }[left];
    from?.focus();
  }
  navDir = 0;
};

const renderHero = () => {
  const d = current();
  const conn = d.conn;
  const connected = conn === 'connected';
  if (d.art !== heroArtKey) {
    heroArtKey = d.art;
    const next = art(d.art);
    next.classList.add('wp-hero__art');
    refs.heroArt.replaceWith(next);
    refs.heroArt = next;
  }
  const multi = m.devices.length > 1;
  setHidden(refs.devSwitch, !multi);
  setHidden(refs.devStatic, multi);
  setText(multi ? refs.devSwitchName : refs.devStaticName, d.name);
  if (!multi && menuOpen) {
    closeMenu(false);
  }

  setAttr(refs.status, 'class', `wp-status wp-status--${STATUS_KIND[conn]}`);
  setText(refs.statusText, connected ? t('status.connectedModel', { model: d.model }) : t(`status.${conn}`));

  const bat = connected ? m.state?.battery : conn === 'reconnecting' ? m.lastBattery.get(d.id) : conn === 'elsewhere' ? d.batteries : null;
  const ear = connected ? m.state?.ear : null;
  const caseGlyph = d.art === 'pro' ? 'case-pro' : 'case';
  updateRing(heroRings.left, bat?.left ?? null, earText(ear, true), caseGlyph);
  updateRing(heroRings.right, bat?.right ?? null, earText(ear, false), caseGlyph);
  updateRing(heroRings.case, bat?.case ?? null, '', caseGlyph);
  updateRing(heroRings.max, bat?.headphone ?? null, '', caseGlyph);
  const anyBattery = !!bat && !!(bat.left || bat.right || bat.case || bat.headphone);
  setHidden(refs.heroBatteries, !anyBattery);
  setClass(refs.hero, 'wp-hero--stale', conn === 'reconnecting');

  const single = d.capSet.has('singleBattery');
  const hint = conn === 'disconnected' ? (single ? 'home.hintMax' : 'home.hintBuds') : conn === 'elsewhere' ? 'home.hintElsewhere' : '';
  setHidden(refs.heroHint, !hint);
  if (hint) {
    setText(refs.heroHint, t(hint));
  }
  setHidden(refs.heroKeysHint, d.hasKeys || connected);

  const action = connected ? ['home.disconnect', 'secondary'] : conn === 'connecting' ? ['home.cancel', 'secondary']
    : conn === 'reconnecting' ? null : ['home.connect', 'primary'];
  setHidden(refs.heroActions, !action);
  if (action) {
    setAttr(refs.heroAction, 'class', `wp-btn wp-btn--${action[1]}`);
    setText(refs.heroAction, t(action[0]));
  }
};

const renderMenu = () => {
  for (const el of refs.menu.querySelectorAll('[data-id]')) {
    el.remove();
  }
  for (const d of m.devices) {
    const checked = d.id === m.selected;
    const el = html(`<button type="button" class="wp-menu__item" role="menuitemradio" tabindex="-1" aria-checked="${checked}">` +
      `<span class="wp-menu__glyph">${WPods.glyph(d.art === 'max' ? 'max' : 'pair')}</span>` +
      `<span class="wp-menu__text"><span class="wp-menu__title"></span><span class="wp-menu__meta"></span></span>` +
      `${WPods.icon('check', 'wp-menu__check')}</button>`);
    el.dataset.id = d.id;
    el.querySelector('.wp-menu__title').textContent = d.name;
    el.querySelector('.wp-menu__meta').textContent = t(`status.${d.conn}`);
    el.lastElementChild.hidden = !checked;
    refs.menu.insertBefore(el, refs.menuSep);
  }
};

const renderNc = () => {
  const d = current();
  const caps = d.capSet;
  const has = caps.has('noiseControl');
  setHidden(refs.ncGroup, !has);
  if (!has) {
    return;
  }
  const st = d.conn === 'connected' ? m.state : null;
  const locked = !st;
  setHidden(refs.ncAdaptiveItem, !caps.has('adaptive'));
  setHidden(refs.ncLocked, !locked);
  const inEar = st?.ear ? (st.ear.primary === 'inEar') + (st.ear.secondary === 'inEar') : 2;
  const oneIn = !locked && !caps.has('singleBattery') && inEar === 1 && st.oneBudAnc !== true;
  setHidden(refs.ncOneBud, !oneIn);
  for (const [v, input] of Object.entries(ncRadios)) {
    const needsBoth = v === 'noiseCancellation' || v === 'adaptive';
    setDisabled(input, locked || (oneIn && needsBoth));
    setAttr(input, 'aria-describedby', locked ? 'ncLocked' : oneIn && needsBoth ? 'ncOneBud' : null);
  }
  checkRadio(ncRadios, st?.listeningMode ?? null);
};

const renderMic = () => {
  const d = current();
  const status = m.mic;
  setAttr(refs.micChip, 'data-state', status);
  setAttr(refs.micChip, 'class', `wp-chip${status === 'live' ? ' wp-chip--live' : status === 'problem' ? ' wp-chip--problem' : ''}`);
  setText(refs.micLabel, t(`mic.status.${status}`));
  setText(refs.micDesc, t(`mic.desc.${status}`));
  checkRadio(micModeRadios, d.host.micMode);
  const st = d.conn === 'connected' ? m.state : null;
  const checking = !!st && st.micSide == null;
  setHidden(refs.micSideCheck, !checking);
  setHidden(refs.micSideSeg, checking);
  setHidden(refs.micSideLocked, !!st);
  lockRadios(micSideRadios, !st, st ? null : 'micSideLocked');
  checkRadio(micSideRadios, st?.micSide ?? null);
};

const renderFeatures = () => {
  const d = current();
  const st = d.conn === 'connected' ? m.state : null;
  const locked = !st;
  const single = d.capSet.has('singleBattery');
  setHidden(refs.featLocked, !locked);
  for (const f of Object.values(features)) {
    const def = f.def;
    const has = !def.cap || d.capSet.has(def.cap);
    setHidden(f.row, !has);
    if (!has) {
      continue;
    }
    let value = st ? st[def.key] : null;
    let reason = false;
    if (def.key === 'heartRate') {
      value = st ? !!HR_ON[st.heartRate] : null;
    } else if (def.key === 'hearingAid') {
      reason = st?.hearingAidEnrolled === false;
      value = st && st.hearingAidEnrolled != null ? st.hearingAidEnabled : null;
    }
    const checking = !locked && !reason && value == null;
    setClass(f.row, 'is-disabled', reason);
    setText(f.desc, t(reason ? 'features.hearingAidSetup' : single && def.descMax ? def.descMax : def.desc));
    setHidden(f.check, !checking);
    setHidden(f.sw, checking);
    setChecked(f.input, !reason && !!value);
    setDisabled(f.input, locked || reason);
    setAttr(f.input, 'aria-describedby', locked ? 'featLocked' : reason ? f.desc.id : null);
  }
  const hrOn = !!st && !!HR_ON[st.heartRate] && d.capSet.has('heartRate');
  const live = hrOn && st.heartRate === 'streaming' && st.heartRateBpm != null;
  setHidden(refs.hrLive, !live);
  setHidden(refs.hrWait, !hrOn || live);
  setHidden(refs.hrNote, !hrOn);
  if (live) {
    setText(refs.hrNum, String(st.heartRateBpm));
    setAttr(refs.hrLive, 'aria-label', t('hr.a11y', { bpm: st.heartRateBpm }));
  }
};

const renderControls = () => {
  const d = current();
  const st = d.conn === 'connected' ? m.state : null;
  const locked = !st;
  setHidden(refs.pressLocked, !locked);
  setHidden(refs.toneLocked, !locked);
  const rows = [
    [pressRadios, refs.pressSpeedSeg, refs.pressSpeedCheck, st?.pressSpeed],
    [holdRadios, refs.holdSeg, refs.holdCheck, st?.holdDuration],
  ];
  for (const [group, seg, check, value] of rows) {
    const checking = !locked && value == null;
    setHidden(check, !checking);
    setHidden(seg, checking);
    lockRadios(group, locked, locked ? 'pressLocked' : null);
    checkRadio(group, value ?? null);
  }
  const tone = st?.toneVolume ?? null;
  const toneChecking = !locked && tone == null;
  setHidden(refs.toneCheck, !toneChecking);
  setHidden(refs.toneSlider, toneChecking);
  setDisabled(refs.tone, locked);
  setAttr(refs.tone, 'aria-describedby', locked ? 'toneLocked' : null);
  if (tone !== null && tone !== lastTone) {
    lastTone = tone;
    refs.tone.value = String(tone);
    refs.tone.style.setProperty('--v', `${tone}%`);
    setText(refs.toneValue, String(tone));
  }
  setHidden(refs.toneValue, tone === null);
};

const renderWindows = () => {
  const d = current();
  setText(refs.windowsFor, t('windows.forDevice', { name: d.name }));
  setChecked(refs.handsFree, d.host.disableHandsFree);
  setChecked(refs.latency, d.host.lowLatency);
  setHidden(refs.latencyNote, !d.host.lowLatency);
  setChecked(refs.startup, m.startup);
  checkRadio(themeRadios, themePref);
  setChecked(refs.accent, useAccent);
  const lang = langByCode.get(m.language);
  setText(refs.langValue, lang ? lang.originalName : t('lang.sameAsWindows'));
};

const renderLangs = () => {
  setAttr(refs.langAuto, 'aria-checked', String(m.language === ''));
  for (const row of langRows) {
    setAttr(row.el, 'aria-checked', String(row.code === m.language));
  }
};

const buildLangs = () => {
  for (const row of langRows) {
    row.el.remove();
  }
  langRows = [];
  const sorted = m.languages.toSorted((a, b) => a.engName.localeCompare(b.engName));
  for (const l of sorted) {
    const el = html(`<button type="button" class="wp-row wp-choice" role="radio" aria-checked="false"><span class="wp-row__text">` +
      `<span class="wp-row__title"></span><span class="wp-row__desc"></span></span>${WPods.icon('check', 'wp-row__check')}</button>`);
    const title = el.querySelector('.wp-row__title');
    title.textContent = l.originalName;
    title.lang = l.code;
    el.querySelector('.wp-row__desc').textContent = l.engName;
    el.dataset.code = l.code;
    refs.langList.insertBefore(el, refs.langNone);
    langRows.push({ el, code: l.code, hay: `${l.originalName}\n${l.engName}`.toLowerCase() });
  }
  setText(refs.langWindows, m.windowsLanguageName);
};

const filterLangs = () => {
  const query = refs.langSearch.value.trim();
  const q = query.toLowerCase();
  let visible = 0;
  for (const row of langRows) {
    const show = !q || row.hay.includes(q);
    setHidden(row.el, !show);
    visible += show ? 1 : 0;
  }
  setHidden(refs.langNone, visible > 0);
  if (visible === 0) {
    setText(refs.langNone, t('lang.noMatch', { query }));
  }
};

/* Toast */
const hideToast = () => {
  clearTimeout(toastTimer);
  setHidden(refs.toast, true);
};
const armToast = () => {
  clearTimeout(toastTimer);
  if (toastKind !== 'error') {
    toastTimer = setTimeout(hideToast, toastMs);
  }
};
const showToast = ({ toast, device, name }) => {
  const def = TOASTS[toast];
  if (!def) {
    return;
  }
  toastKind = def.kind;
  toastDevice = device ?? m.selected;
  toastAction = def.action;
  const el = refs.toast;
  el.className = `wp-toast wp-toast--${def.kind}`;
  el.setAttribute('role', def.kind === 'error' ? 'alert' : 'status');
  setText(refs.toastMsg, t(`toast.${toast}`, { name: name ?? '' }));
  setHidden(refs.toastAction, !def.action);
  setHidden(refs.toastClose, !!def.action);
  if (def.action) {
    setText(refs.toastAction, t(`toast.action.${def.action}`));
  }
  el.hidden = false;
  refs.win.insertBefore(el, refs.announce);
  armToast();
};

/* Frame */
const RUN = { hero: renderHero, nc: renderNc, mic: renderMic, features: renderFeatures, controls: renderControls, windows: renderWindows, langs: renderLangs };
const render = () => {
  if (pendingAnnounce) {
    setText(refs.announce, pendingAnnounce);
    pendingAnnounce = '';
  }
  if (pendingToast) {
    showToast(pendingToast);
    pendingToast = null;
  }
  if (!ready) {
    return;
  }
  if (dirty.delete('page')) {
    renderPage();
  }
  if (shown === 'firstRun' && dirty.delete('firstRun')) {
    setChecked(refs.frStartup, m.startup);
  }
  if (!current()) {
    return;
  }
  for (const s of SECTIONS[shown]) {
    if (dirty.delete(s)) {
      RUN[s]();
    }
  }
  if (menuOpen && dirty.delete('menu')) {
    renderMenu();
  }
};

/* Messages */
const applyDevices = (devices, attached) => {
  m.devices = devices;
  m.byId.clear();
  for (const d of devices) {
    d.capSet = new Set(d.caps);
    m.byId.set(d.id, d);
  }
  if (attached !== m.attached) {
    m.attached = attached;
    m.lowSaid.clear();
    if (attached) {
      m.selected = attached;
    }
  }
  if (!m.byId.has(m.selected)) {
    m.selected = m.attached ?? devices[0]?.id ?? null;
  }
  mark('page', 'hero', 'nc', 'mic', 'features', 'controls', 'windows', 'menu');
};

const applyBattery = (battery) => {
  if (!m.state) {
    return;
  }
  m.state.battery = battery;
  m.lastBattery.set(m.attached, battery);
  const name = m.byId.get(m.attached)?.name ?? '';
  for (const [part, ring] of Object.entries(BATTERY_PARTS)) {
    const b = battery?.[part];
    if (!b) {
      continue;
    }
    const low = !b.charging && b.pct < 10;
    if (!low) {
      m.lowSaid.delete(part);
    } else if (!m.lowSaid.has(part)) {
      m.lowSaid.add(part);
      pendingAnnounce = t('battery.low', { name, part: t(`battery.${ring}`), pct: b.pct });
    }
  }
  mark('hero');
};

const apply = (msg) => {
  applyCommon(msg);
  switch (msg.type) {
    case 'init':
      applyLabels(document);
      for (const p of Object.values(pages)) {
        applyLabels(p);
      }
      pages.empty.querySelector('.wp-empty__art').setAttribute('aria-label', t('art.empty'));
      m.startup = msg.startup;
      m.firstRun = msg.firstRun;
      m.language = msg.language ?? '';
      m.languages = msg.languages ?? [];
      langByCode.clear();
      for (const l of m.languages) {
        langByCode.set(l.code, l);
      }
      m.windowsLanguageName = msg.windowsLanguageName ?? '';
      m.mic = msg.mic;
      m.state = msg.state;
      m.attached = msg.attached ?? null;
      m.selected = msg.selected ?? m.attached;
      applyDevices(msg.devices, m.attached);
      if (m.state?.battery) {
        m.lastBattery.set(m.attached, m.state.battery);
      }
      toastMs = parseFloat(getComputedStyle(root).getPropertyValue('--dur-toast')) || toastMs;
      buildLangs();
      shown = null;
      ready = true;
      mark('firstRun', 'langs');
      break;
    case 'devices':
      applyDevices(msg.devices, msg.attached ?? null);
      break;
    case 'state':
      m.state = msg.state;
      m.lowSaid.clear();
      if (msg.state?.battery) {
        applyBattery(msg.state.battery);
      }
      mark('hero', 'nc', 'mic', 'features', 'controls');
      break;
    case 'battery':
      applyBattery(msg.battery);
      break;
    case 'ear':
      if (m.state) {
        m.state.ear = msg.ear;
        mark('hero', 'nc');
      }
      break;
    case 'listeningMode':
      if (m.state) {
        m.state.listeningMode = msg.listeningMode;
        mark('nc');
      }
      break;
    case 'settings':
      if (m.state) {
        Object.assign(m.state, msg.settings);
        mark('nc', 'mic', 'features', 'controls');
      }
      break;
    case 'heartRate':
      if (m.state) {
        m.state.heartRate = msg.heartRate;
        m.state.heartRateBpm = msg.heartRateBpm;
        mark('features');
      }
      break;
    case 'hostSettings': {
      const d = m.byId.get(msg.id);
      if (d) {
        d.host = msg.host;
        mark('mic', 'windows');
      }
      break;
    }
    case 'mic':
      m.mic = msg.status;
      mark('mic');
      break;
    case 'prefs':
      if (msg.startup !== undefined) {
        m.startup = msg.startup;
      }
      mark('windows', 'firstRun');
      break;
    case 'toast':
      pendingToast = msg;
      break;
    default:
      break;
  }
};
const schedule = listen(apply, render);
const update = (...names) => {
  mark(...names);
  schedule();
};

/* Navigation and menu */
const go = (p) => {
  closeMenu(false);
  page = p;
  navDir = 1;
  update('page');
};
const back = () => {
  if (shown === 'language') {
    refs.langSearch.value = '';
    filterLangs();
  }
  page = PARENT[shown] ?? 'home';
  navDir = -1;
  update('page');
};
const openMenu = () => {
  menuOpen = true;
  renderMenu();
  dirty.delete('menu');
  refs.menu.hidden = false;
  refs.devSwitch.setAttribute('aria-expanded', 'true');
  (refs.menu.querySelector('[aria-checked="true"]') ?? refs.menuPair).focus();
};
const closeMenu = (refocus) => {
  if (!menuOpen) {
    return;
  }
  menuOpen = false;
  refs.menu.hidden = true;
  refs.devSwitch.setAttribute('aria-expanded', 'false');
  if (refocus) {
    refs.devSwitch.focus();
  }
};

refs.devSwitch.addEventListener('click', () => {
  if (menuOpen) {
    closeMenu(true);
  } else {
    openMenu();
  }
});
refs.devSwitch.addEventListener('keydown', (e) => {
  if (e.key === 'ArrowDown' && !menuOpen) {
    e.preventDefault();
    openMenu();
  }
});
refs.menu.addEventListener('click', (e) => {
  const item = e.target.closest('.wp-menu__item');
  if (!item) {
    return;
  }
  if (item === refs.menuPair) {
    go('pair');
    return;
  }
  m.selected = item.dataset.id;
  closeMenu(true);
  update('hero', 'nc', 'mic', 'features', 'controls', 'windows', 'menu');
});
refs.menu.addEventListener('keydown', (e) => {
  const items = refs.menu.querySelectorAll('.wp-menu__item');
  let at = -1;
  for (const [i, el] of items.entries()) {
    if (el === document.activeElement) {
      at = i;
      break;
    }
  }
  let next = -1;
  if (e.key === 'ArrowDown') {
    next = (at + 1) % items.length;
  } else if (e.key === 'ArrowUp') {
    next = (at - 1 + items.length) % items.length;
  } else if (e.key === 'Home') {
    next = 0;
  } else if (e.key === 'End') {
    next = items.length - 1;
  } else if (e.key === 'Tab') {
    closeMenu(false);
  }
  if (next >= 0) {
    e.preventDefault();
    items[next].focus();
  }
});
document.addEventListener('pointerdown', (e) => {
  if (menuOpen && !refs.menu.contains(e.target) && !refs.devSwitch.contains(e.target)) {
    closeMenu(false);
  }
});
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') {
    return;
  }
  if (menuOpen) {
    e.preventDefault();
    closeMenu(true);
  } else if (!refs.toast.hidden && refs.toast.contains(document.activeElement)) {
    hideToast();
  } else if (e.target === refs.langSearch && refs.langSearch.value) {
    /* the search field clears itself first */
  } else if (PARENT[shown]) {
    back();
  }
});

/* Controls -> host */
refs.back.addEventListener('click', back);
refs.minimize.addEventListener('click', () => post({ type: 'minimize' }));
refs.close.addEventListener('click', () => post({ type: 'close' }));
refs.titlebar.addEventListener('mousedown', (e) => {
  if (e.button === 0 && !e.target.closest('button')) {
    e.preventDefault();
    post({ type: 'beginDrag' });
  }
});
refs.toControls.addEventListener('click', () => go('controls'));
refs.toWindows.addEventListener('click', () => go('windows'));
refs.toLanguage.addEventListener('click', () => go('language'));
refs.openBluetooth.addEventListener('click', () => post({ type: 'openBluetoothSettings' }));
refs.addDevice.addEventListener('click', () => post({ type: 'openAddDevice' }));
refs.getStarted.addEventListener('click', () => {
  m.firstRun = false;
  post({ type: 'getStarted' });
  update('page');
});
refs.heroAction.addEventListener('click', () => {
  const d = current();
  const type = d.conn === 'connected' ? 'disconnect' : d.conn === 'connecting' ? 'cancel' : 'connect';
  post({ type, id: d.id });
});

/* Optimistic: the control already shows the new value; the host echoes the real one (also on failure). */
const setState = (field, value) => {
  if (m.state) {
    m.state[field] = value;
  }
};
refs.ncSeg.addEventListener('change', (e) => {
  setState('listeningMode', e.target.value);
  post({ type: 'setListeningMode', mode: e.target.value });
});
refs.micModeSeg.addEventListener('change', (e) => {
  const d = current();
  d.host.micMode = e.target.value;
  post({ type: 'setHostSetting', id: d.id, name: 'micMode', value: e.target.value });
});
refs.micSideSeg.addEventListener('change', (e) => {
  setState('micSide', e.target.value);
  post({ type: 'setMicSide', side: e.target.value });
});
refs.pressSpeedSeg.addEventListener('change', (e) => {
  setState('pressSpeed', e.target.value);
  post({ type: 'setPressSpeed', value: e.target.value });
});
refs.holdSeg.addEventListener('change', (e) => {
  setState('holdDuration', e.target.value);
  post({ type: 'setHoldDuration', value: e.target.value });
});
refs.tone.addEventListener('input', () => {
  refs.tone.style.setProperty('--v', `${refs.tone.value}%`);
  setText(refs.toneValue, refs.tone.value);
});
refs.tone.addEventListener('change', () => {
  const value = Number(refs.tone.value);
  setState('toneVolume', value);
  lastTone = value;
  post({ type: 'setToneVolume', value });
});
refs.features.addEventListener('change', (e) => {
  const key = e.target.dataset.feature;
  const value = e.target.checked;
  if (key === 'heartRate') {
    setState('heartRate', value ? 'starting' : 'off');
    post({ type: 'setHeartRate', value });
  } else if (key === 'hearingAid') {
    setState('hearingAidEnabled', value);
    post({ type: 'setHearingAid', value });
  } else {
    setState(key, value);
    post({ type: 'setToggle', name: key, value });
  }
  update('features', 'nc');
});
const hostToggle = (input, name) => {
  input.addEventListener('change', () => {
    const d = current();
    d.host[name] = input.checked;
    post({ type: 'setHostSetting', id: d.id, name, value: input.checked });
    update('windows');
  });
};
hostToggle(refs.handsFree, 'disableHandsFree');
hostToggle(refs.latency, 'lowLatency');
for (const input of [refs.startup, refs.frStartup]) {
  input.addEventListener('change', () => {
    m.startup = input.checked;
    post({ type: 'setStartup', value: input.checked });
  });
}
refs.themeSeg.addEventListener('change', (e) => {
  themePref = e.target.value;
  applyTheme();
  post({ type: 'setTheme', theme: themePref });
});
refs.accent.addEventListener('change', () => {
  useAccent = refs.accent.checked;
  applyTheme();
  post({ type: 'setAccent', value: useAccent });
});
const pickLanguage = (code) => {
  if (code === m.language) {
    return;
  }
  m.language = code;
  post({ type: 'setLanguage', code });
  update('langs', 'windows');
};
refs.langAuto.addEventListener('click', () => pickLanguage(''));
refs.langList.addEventListener('click', (e) => {
  const row = e.target.closest('[data-code]');
  if (row) {
    pickLanguage(row.dataset.code);
  }
});
refs.langSearch.addEventListener('input', filterLangs);

refs.toastAction.addEventListener('click', () => {
  hideToast();
  if (toastAction === 'openSettings') {
    post({ type: 'openBluetoothSettings' });
  } else if (toastAction === 'tryAgain') {
    post({ type: 'connect', id: toastDevice });
  } else {
    post({ type: 'reconnect', id: toastDevice });
  }
});
refs.toastClose.addEventListener('click', hideToast);
refs.toast.addEventListener('pointerenter', () => clearTimeout(toastTimer));
refs.toast.addEventListener('focusin', () => clearTimeout(toastTimer));
refs.toast.addEventListener('pointerleave', armToast);
refs.toast.addEventListener('focusout', armToast);

post({ type: 'ready', page: 'main' });
