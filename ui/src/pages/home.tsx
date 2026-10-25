/* Home: hero (art, switcher, status, batteries, action), noise control, microphone (mode, enhancement, side), features,
   More, the legal footer. */
import { createBatteries } from '../components/batteryRing';
import { createBusy, NavRow, Note } from '../components/common';
import { createDeviceMenu } from '../components/deviceMenu';
import { createSegmented } from '../components/segmented';
import { createSwitchRow, type SwitchRow } from '../components/switchRow';
import { setAttr, setClass, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t, type LabelKey } from '../i18n';
import { Art, Icon, Spinner } from '../icons';
import type { Capability, Conn, HeartRate, ListeningMode, MicMode, MicSide, State, ToggleName } from '../protocol';
import { beginDisconnect, capsOf, current, effectiveMode, live, store } from '../state';
import { enhancementName } from './enhancement';
import { LegalFoot } from './legal';
import type { Ctx, Page } from './page';

type FeatureKey = ToggleName | 'heartRate' | 'hearingAid';
/* `podSaved`: the AirPods keep the value; it shows the last value (still usable) while checking, the group title spins. */
type FeatureDef = { key: FeatureKey; title: LabelKey; desc: LabelKey; descMax?: LabelKey; podSaved?: boolean };
type Feature = SwitchRow & { def: FeatureDef };
type Side = 'left' | 'right';
type PickerRow = { el: HTMLButtonElement; desc: HTMLSpanElement };

const STATUS_KIND: Record<Conn, string> = {
  connected: 'connected',
  notConnected: 'disconnected',
  attachFailed: 'disconnected',
  elsewhere: 'nearby',
  connecting: 'busy',
  reconnecting: 'busy',
};
const EAR_KEY = { inEar: 'ear.inEar', out: 'ear.out', inCase: 'ear.inCase' } as const;

const HR_ON = new Set<HeartRate>(['starting', 'streaming', 'noReading']);
const CONTROLS_CAPS: Capability[] = ['listeningMode', 'pressSpeed', 'holdDuration', 'conversationAwareness', 'toneVolume'];
const FEATURES: FeatureDef[] = [
  { key: 'earDetection', title: 'features.ear', desc: 'features.earDesc', descMax: 'features.earDescMax' },
  { key: 'conversationAwareness', title: 'features.conversation', desc: 'features.conversationDesc', podSaved: true },
  { key: 'oneBudAnc', title: 'features.oneBud', desc: 'features.oneBudDesc', podSaved: true },
  { key: 'personalizedVolume', title: 'features.personalizedVolume', desc: 'features.personalizedVolumeDesc', podSaved: true },
  { key: 'allowOff', title: 'features.allowOff', desc: 'features.allowOffDesc', descMax: 'features.allowOffDescMax' },
  { key: 'heartRate', title: 'features.heartRate', desc: 'features.heartRateDesc' },
  { key: 'hearingAid', title: 'features.hearingAid', desc: 'features.hearingAidDesc', podSaved: true },
];

/* Ear line under one bud ring; '' (hidden) for null, 'disconnected' and AirPods Max (the host sends null there). */
const earText = (st: State | null, side: Side): string => {
  const ear = st ? st.ear[side] : null;
  return ear === 'inEar' || ear === 'out' || ear === 'inCase' ? t(EAR_KEY[ear]) : '';
};
/* Navigation row to the enhancement page; its description shows the picked entry. */
const PickerRow = (title: LabelKey, onclick: () => void): PickerRow => {
  let desc!: HTMLSpanElement;
  const el = (
    <button type="button" class="wp-row wp-row--nav" onclick={onclick}>
      <span class="wp-row__text"><span class="wp-row__title" l:text={title} /><span class="wp-row__desc" ref={desc} /></span>
      <span class="wp-row__end"><Icon name="chevron-right" /></span>
    </button>
  ) as HTMLButtonElement;
  return { el, desc };
};

/* A bud counts as present when its battery is reported and it isn't 'disconnected'. */
const budPresent = (st: State, side: Side): boolean => st.battery[side] !== null && st.ear[side] !== 'disconnected';

export const createHome = (ctx: Ctx): Page => {
  /* Optimistic: the control already shows the new value; the host echoes the real one (also on failure). */
  const setState = <K extends 'listeningMode' | 'micSide' | 'heartRate' | 'hearingAidEnabled' | ToggleName>(
    field: K, value: NonNullable<typeof store.state>[K]): void => {
    if (store.state) {
      store.state[field] = value;
    }
  };

  /* Hero */
  let hero!: HTMLElement;
  let status!: HTMLSpanElement;
  let statusText!: HTMLSpanElement;
  let hint!: HTMLParagraphElement;
  let keysHint!: HTMLParagraphElement;
  let actions!: HTMLDivElement;
  let action!: HTMLButtonElement;
  let art = Art({ model: 'pro', class: 'wp-hero__art' });
  let artModel = 'pro';
  const menu = createDeviceMenu({
    onPick: (id) => {
      store.selected = id;
      ctx.update('hero', 'menu', 'nc', 'mic', 'features', 'controls', 'windows');
    },
    onPair: () => ctx.go('pair'),
  });
  const batteries = createBatteries(false);

  /* Noise control */
  let ncGroup!: HTMLElement;
  const ncOneBud = Note({ label: 'nc.oneBudNote', id: 'ncOneBud' });
  ncOneBud.hidden = true;
  const ncOutOfEar = Note({ label: 'nc.outOfEarNote', id: 'ncOutOfEar' });
  ncOutOfEar.hidden = true;
  const nc = createSegmented<ListeningMode>({
    name: 'nc',
    legend: 'nc.title',
    icons: true,
    items: [
      /* Display order = the order the stem cycles on the AirPods (Round 19). */
      { value: 'off', label: 'nc.off', icon: 'nc-off' },
      { value: 'transparency', label: 'nc.transparency', icon: 'nc-transparency' },
      { value: 'adaptive', label: 'nc.adaptive', icon: 'nc-adaptive' },
      { value: 'noiseCancellation', label: 'nc.noiseCancellation', icon: 'nc-anc' },
    ],
    onChange: (mode) => {
      setState('listeningMode', mode);
      post({ type: 'setListeningMode', mode });
    },
  });
  /* Pod-saved: spinner while checking (in the group title), blocked until the session is ready. */
  const ncBusy = createBusy(nc.el, nc.el.elements);

  /* Microphone */
  let chip!: HTMLSpanElement;
  let chipLabel!: HTMLSpanElement;
  let micDesc!: HTMLSpanElement;
  let micSideRow!: HTMLDivElement;
  let micModeRow!: HTMLDivElement;
  let micModeSpin!: HTMLSpanElement;
  /* Mic mode: the enabled modes (Mic modes page in Windows settings), showing the effective one; the spinner keeps
     its space while the mode's model loads (the old one keeps running, so picking stays open). */
  const micMode = createSegmented<MicMode>({
    name: 'micMode',
    legend: 'mic.mode',
    items: [
      { value: 'standard', label: 'mic.standard' },
      { value: 'voiceIsolation', label: 'mic.voiceIsolation' },
      { value: 'wideSpectrum', label: 'mic.wideSpectrum' },
    ],
    onChange: (mode) => {
      const d = current();
      if (d) {
        d.host.micMode = mode;
        post({ type: 'setHostSetting', id: d.id, name: 'micMode', value: mode });
        ctx.update('mic');
      }
    },
  });
  /* Voice enhancement: app-wide, never locked. */
  const toEnhancement = PickerRow('mic.enhancement', () => ctx.go('enhancement'));
  const micSide = createSegmented<MicSide>({
    name: 'micSide',
    legend: 'mic.side',
    items: [
      { value: 'automatic', label: 'mic.automatic' },
      { value: 'left', label: 'mic.left' },
      { value: 'right', label: 'mic.right' },
    ],
    onChange: (side) => {
      setState('micSide', side);
      post({ type: 'setMicSide', side });
    },
  });

  /* Features */
  let featGroup!: HTMLElement;
  let featTitle!: HTMLHeadingElement;
  let featList!: HTMLDivElement;
  const features: Feature[] = [];
  const onFeature = (key: FeatureKey, value: boolean): void => {
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
    ctx.update('features', 'nc');
  };
  for (const def of FEATURES) {
    const row = createSwitchRow({ id: `f-${def.key}`, title: def.title, desc: null, stack: true, checking: def.podSaved, onChange: (on) => onFeature(def.key, on) });
    row.row.hidden = true;
    features.push({ ...row, def });
  }
  let hrNum!: HTMLSpanElement;
  const hrNote = Note({ label: 'hr.note' });
  hrNote.hidden = true;
  const hrLive = (
    <div class="wp-hr" role="status" aria-live="off" hidden>
      <span class="wp-hr__heart"><Icon name="heart" class="wp-icon--lg" /></span>
      <span class="wp-hr__value"><span class="wp-hr__num" ref={hrNum} /><span class="wp-hr__unit" l:text="hr.unit" /></span>
    </div>
  ) as HTMLDivElement;
  const hrWait = (
    <div class="wp-hr wp-hr--waiting" hidden>
      <span class="wp-hr__heart"><Icon name="heart" class="wp-icon--lg" /></span>
      <span class="wp-hr__wait" l:text="hr.waiting" />
    </div>
  ) as HTMLDivElement;
  features.find((f) => f.def.key === 'heartRate')?.row.append(hrLive, hrWait, hrNote);

  /* More */
  const toControls = NavRow({ icon: 'press', title: 'controls.title', desc: 'home.controlsDesc', onclick: () => ctx.go('controls') });
  const toWindows = NavRow({ icon: 'pc', title: 'windows.title', desc: 'home.windowsDesc', onclick: () => ctx.go('windows') });
  const legal = LegalFoot(ctx);

  const el = (
    <div class="wp-page">
      <section class="wp-card wp-hero" l:aria-label="home.yourAirPods" ref={hero}>
        {art}
        <div class="wp-hero__id">
          {menu.el}
          <span class="wp-status" role="status" ref={status}>
            <Spinner class="wp-spinner--sm" />
            <span class="wp-status__dot" />
            <span ref={statusText} />
          </span>
        </div>
        {batteries.el}
        <p class="wp-hero__hint" hidden ref={hint} />
        <p class="wp-hero__hint" l:text="home.keysHint" hidden ref={keysHint} />
        <div class="wp-hero__actions" ref={actions}>
          <button type="button" class="wp-btn wp-btn--primary" ref={action}
            onclick={() => {
              const d = current();
              if (!d) {
                return;
              }
              if (d.conn === 'connected') {
                /* Optimistic and immediate: Not connected at once, never Reconnecting; reverted only on disconnectFailed. */
                beginDisconnect(d.id);
                ctx.update('hero', 'menu', 'nc', 'mic', 'features', 'controls', 'windows');
                post({ type: 'disconnect', id: d.id });
              } else {
                post({ type: d.conn === 'connecting' ? 'cancel' : 'connect', id: d.id });
              }
            }} />
        </div>
      </section>

      <section class="wp-group" ref={ncGroup}>
        <h2 class="wp-group__title"><span l:text="nc.title" />{ncBusy.spin}</h2>
        <div class="wp-list">
          <div class="wp-row wp-row--stack">{nc.el}{ncOneBud}{ncOutOfEar}</div>
        </div>
      </section>

      <section class="wp-group">
        <h2 class="wp-group__title">
          <span l:text="mic.title" />
          <span class="wp-chip" role="status" ref={chip}>
            <Spinner class="wp-spinner--sm" />
            <Icon name="warning" class="wp-icon--sm" />
            <span class="wp-chip__dot" />
            <span ref={chipLabel} />
          </span>
        </h2>
        <div class="wp-list">
          <div class="wp-row"><div class="wp-row__text"><span class="wp-row__desc" ref={micDesc} /></div></div>
          <div class="wp-row wp-row--stack" ref={micModeRow}>
            <div class="wp-row__head">
              <div class="wp-row__text"><span class="wp-row__title" l:text="mic.mode" /></div>
              <span class="wp-row__end wp-busy" ref={micModeSpin}><Spinner class="wp-spinner--sm" /></span>
            </div>
            {micMode.el}
          </div>
          {toEnhancement.el}
          <div class="wp-row">{Note({ label: 'mic.note' })}</div>
          <div class="wp-row wp-row--stack" ref={micSideRow}>
            <div class="wp-row__head">
              <div class="wp-row__text"><span class="wp-row__title" l:text="mic.side" /><span class="wp-row__desc" l:text="mic.sideDesc" /></div>
            </div>
            {micSide.el}
          </div>
        </div>
      </section>

      <section class="wp-group" ref={featGroup}>
        <h2 class="wp-group__title" ref={featTitle}><span l:text="features.title" /></h2>
        <div class="wp-list" ref={featList} />
      </section>

      <section class="wp-group">
        <h2 class="wp-group__title" l:text="home.more" />
        <div class="wp-list">{toControls}{toWindows}</div>
      </section>

      {legal.el}
    </div>
  ) as HTMLDivElement;
  for (const f of features) {
    featList.append(f.row);
  }
  /* One spinner for the whole group while checking (in the title); each pod-saved row blocks itself. */
  const featBusy = createBusy(featList, []);
  featTitle.append(featBusy.spin);

  const renderHero = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const conn = d.conn;
    const st = live(d);
    if (d.art !== artModel) {
      artModel = d.art;
      const next = Art({ model: d.art, class: 'wp-hero__art' });
      art.replaceWith(next);
      art = next;
    }
    setAttr(art, 'aria-label', t(`art.${d.art}`));

    setAttr(status, 'class', `wp-status wp-status--${STATUS_KIND[conn]}`);
    setText(statusText, conn === 'connected' ? t('status.connectedModel', { model: d.model }) : t(`status.${conn}`));

    const bat = st ? st.battery : conn === 'reconnecting' ? store.lastBattery.get(d.id) ?? null : conn === 'elsewhere' ? d.batteries : null;
    const any = batteries.update(bat, { left: earText(st, 'left'), right: earText(st, 'right') }, d.art === 'pro' ? 'case-pro' : 'case');
    setHidden(batteries.el, !any);
    setClass(hero, 'wp-hero--stale', conn === 'reconnecting');

    const single = capsOf(d).has('singleBattery');
    const hintKey = conn === 'notConnected' ? (single ? 'home.hintMax' : 'home.hintBuds') : conn === 'elsewhere' ? 'home.hintElsewhere' : null;
    setHidden(hint, !hintKey);
    if (hintKey) {
      setText(hint, t(hintKey));
    }
    setHidden(keysHint, d.hasKeys || conn === 'connected');

    const act = conn === 'connected' ? (['home.disconnect', 'secondary'] as const)
      : conn === 'connecting' ? (['home.cancel', 'secondary'] as const)
        : conn === 'reconnecting' ? null
          : conn === 'attachFailed' ? (['home.reconnect', 'primary'] as const) : (['home.connect', 'primary'] as const);
    setHidden(actions, !act);
    if (act) {
      setAttr(action, 'class', `wp-btn wp-btn--${act[1]}`);
      setText(action, t(act[0]));
    }
  };

  const renderNc = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const has = caps.has('listeningMode');
    setHidden(ncGroup, !has);
    if (!has) {
      return;
    }
    const st = live(d);
    nc.showItem('off', !(caps.has('allowOff') && (st ? st.allowOff : d.saved.allowOff) === false));
    nc.showItem('adaptive', caps.has('adaptive'));
    /* Round 17: NC and Adaptive need both buds unless "Noise cancellation with one AirPod" is on: locked when fewer
       than 2 buds are present OR exactly one is in ear. Off and Transparency stay available. */
    /* With no bud in an ear (one out or in the case) the AirPods force Transparency and ignore mode writes until a
       bud goes back in: every mode is locked. */
    let outOfEar = false;
    if (st && !caps.has('singleBattery')) {
      const { left, right } = st.ear;
      outOfEar = left !== 'inEar' && right !== 'inEar'
        && (left === 'out' || left === 'inCase' || right === 'out' || right === 'inCase');
    }
    let oneIn = false;
    if (st && !outOfEar && !caps.has('singleBattery') && st.oneBudAnc !== true) {
      const present = Number(budPresent(st, 'left')) + Number(budPresent(st, 'right'));
      const inEar = Number(st.ear.left === 'inEar') + Number(st.ear.right === 'inEar');
      oneIn = present < 2 || inEar === 1;
    }
    setHidden(ncOneBud, !oneIn);
    setHidden(ncOutOfEar, !outOfEar);
    ncBusy.set(!!st && st.checking, !!st && !st.ready);
    for (const mode of ['off', 'noiseCancellation', 'transparency', 'adaptive'] as const) {
      const needsBoth = mode === 'noiseCancellation' || mode === 'adaptive';
      nc.lockItem(mode, !st || outOfEar || (oneIn && needsBoth),
        !st ? null : outOfEar ? 'ncOutOfEar' : oneIn && needsBoth ? 'ncOneBud' : null);
    }
    nc.set(st ? st.listeningMode : d.saved.listeningMode);
  };

  const renderMic = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const mic = store.mic;
    setAttr(chip, 'data-state', mic);
    setAttr(chip, 'class', `wp-chip${mic === 'live' ? ' wp-chip--live' : mic === 'problem' ? ' wp-chip--problem' : ''}`);
    setText(chipLabel, t(`mic.status.${mic}`));
    setText(micDesc, t(`mic.desc.${mic}`));
    const st = live(d);
    const enabled = store.micEnabled;
    setHidden(micModeRow, enabled.size < 2);
    for (const mode of ['standard', 'voiceIsolation', 'wideSpectrum'] as const) {
      micMode.showItem(mode, enabled.has(mode));
    }
    micMode.lock(!st, null);
    micMode.set(effectiveMode(d.host.micMode));
    const loading = !!st && store.micLoading.mode !== null;
    setClass(micModeSpin, 'is-busy', loading);
    setAttr(micMode.el, 'aria-busy', loading ? 'true' : null);
    setText(toEnhancement.desc, enhancementName(store.micConfig.enhancement));
    setHidden(micSideRow, !capsOf(d).has('micSide'));
    micSide.lock(!st || !st.ready, null);
    /* Not reported = the device default, Automatic (Round 19). */
    micSide.set((st ? st.micSide : d.saved.micSide) ?? 'automatic');
  };

  const renderFeatures = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
    const pods = st ?? d.saved;
    const checking = !!st && st.checking;
    const notReady = !!st && !st.ready;
    const single = caps.has('singleBattery');
    let any = false;
    for (const f of features) {
      const def = f.def;
      const has = caps.has(def.key);
      setHidden(f.row, !has);
      if (!has) {
        continue;
      }
      any = true;
      let value: boolean | null = null;
      let reason = false;
      if (def.key === 'heartRate') {
        value = st ? HR_ON.has(st.heartRate) : null;
      } else if (def.key === 'hearingAid') {
        reason = pods.hearingAidEnrolled === false;
        value = pods.hearingAidEnrolled !== null ? pods.hearingAidEnabled : null;
      } else {
        value = pods[def.key];
      }
      setClass(f.row, 'is-disabled', reason);
      setText(f.desc, t(reason ? 'features.hearingAidSetup' : single && def.descMax ? def.descMax : def.desc));
      /* Hearing aid stays blocked while checking: it depends on the setup done on a Mac or iPhone. */
      f.busy?.set(checking, notReady || (def.key === 'hearingAid' && checking));
      if (f.input.checked !== (!reason && !!value)) {
        f.input.checked = !reason && !!value;
      }
      setDisabled(f.input, !st || reason || (!f.busy && notReady));
      setAttr(f.input, 'aria-describedby', st && reason ? f.desc.id : null);
    }
    featBusy.set(checking, false);
    setHidden(featGroup, !any);

    const hrOn = !!st && HR_ON.has(st.heartRate) && caps.has('heartRate');
    const bpm = st?.heartRateBpm ?? null;
    const showBpm = hrOn && st?.heartRate === 'streaming' && bpm !== null;
    setHidden(hrLive, !showBpm);
    setHidden(hrWait, !hrOn || showBpm);
    setHidden(hrNote, !hrOn);
    if (showBpm) {
      setText(hrNum, String(bpm));
      setAttr(hrLive, 'aria-label', t('hr.a11y', { bpm }));
    }

    let controls = false;
    for (const cap of CONTROLS_CAPS) {
      if (caps.has(cap)) {
        controls = true;
        break;
      }
    }
    setHidden(toControls, !controls);
  };

  return {
    el,
    sections: {
      hero: renderHero,
      menu: () => {
        const d = current();
        if (d) {
          menu.render(store.devices, d);
        }
      },
      nc: renderNc,
      mic: renderMic,
      features: renderFeatures,
    },
    returnFocus: (from) => {
      switch (from) {
        case 'controls':
          return toControls;
        case 'windows':
          return toWindows;
        case 'pair':
          return menu.button;
        case 'enhancement':
          return toEnhancement.el;
        case 'legal':
          return legal.link;
        default:
          return null;
      }
    },
  };
};
