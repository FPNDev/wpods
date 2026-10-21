/* Home: hero (art, switcher, status, batteries, action), noise control, microphone, features, More. */
import { createBatteries } from '../components/batteryRing';
import { Checking, LockRow, NavRow, Note } from '../components/common';
import { createDeviceMenu } from '../components/deviceMenu';
import { createSegmented } from '../components/segmented';
import { createSwitchRow, type SwitchRow } from '../components/switchRow';
import { setAttr, setClass, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t, type LabelKey } from '../i18n';
import { Art, Icon, Spinner } from '../icons';
import type { Capability, Conn, EarState, HeartRate, ListeningMode, MicMode, MicSide, ToggleName } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Ctx, Page } from './page';

type FeatureKey = ToggleName | 'heartRate' | 'hearingAid';
type FeatureDef = { key: FeatureKey; title: LabelKey; desc: LabelKey; descMax?: LabelKey };
type Feature = SwitchRow & { def: FeatureDef };

const STATUS_KIND: Record<Conn, string> = { connected: 'connected', disconnected: 'disconnected', elsewhere: 'nearby', connecting: 'busy', reconnecting: 'busy' };
const EAR_KEY = { inEar: 'ear.inEar', outOfEar: 'ear.out', inCase: 'ear.inCase' } as const;
const HR_ON = new Set<HeartRate>(['starting', 'streaming', 'noReading']);
const CONTROLS_CAPS: Capability[] = ['pressSpeed', 'holdDuration', 'toneVolume'];
const FEATURES: FeatureDef[] = [
  { key: 'earDetection', title: 'features.ear', desc: 'features.earDesc', descMax: 'features.earDescMax' },
  { key: 'conversationAwareness', title: 'features.conversation', desc: 'features.conversationDesc' },
  { key: 'oneBudAnc', title: 'features.oneBud', desc: 'features.oneBudDesc' },
  { key: 'personalizedVolume', title: 'features.personalizedVolume', desc: 'features.personalizedVolumeDesc' },
  { key: 'allowOff', title: 'features.allowOff', desc: 'features.allowOffDesc', descMax: 'features.allowOffDescMax' },
  { key: 'heartRate', title: 'features.heartRate', desc: 'features.heartRateDesc' },
  { key: 'hearingAid', title: 'features.hearingAid', desc: 'features.hearingAidDesc' },
];

/* Ear text for one bud; '' while the core can't tell which bud is left (primaryLeft null). */
const earText = (ear: EarState | null, left: boolean): string => {
  if (!ear || ear.primaryLeft === null) {
    return '';
  }
  const status = ear.primaryLeft === left ? ear.primary : ear.secondary;
  return status === 'inEar' || status === 'outOfEar' || status === 'inCase' ? t(EAR_KEY[status]) : '';
};

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
  const ncLocked = Note({ label: 'common.connectToChange', id: 'ncLocked' });
  const ncOneBud = Note({ label: 'nc.oneBudNote', id: 'ncOneBud' });
  ncOneBud.hidden = true;
  const nc = createSegmented<ListeningMode>({
    name: 'nc',
    legend: 'nc.title',
    icons: true,
    items: [
      { value: 'off', label: 'nc.off', icon: 'nc-off' },
      { value: 'noiseCancellation', label: 'nc.noiseCancellation', icon: 'nc-anc' },
      { value: 'transparency', label: 'nc.transparency', icon: 'nc-transparency' },
      { value: 'adaptive', label: 'nc.adaptive', icon: 'nc-adaptive' },
    ],
    onChange: (mode) => {
      setState('listeningMode', mode);
      post({ type: 'setListeningMode', mode });
    },
  });

  /* Microphone */
  let chip!: HTMLSpanElement;
  let chipLabel!: HTMLSpanElement;
  let micDesc!: HTMLSpanElement;
  let micSideRow!: HTMLDivElement;
  const micLocked = LockRow('micLocked');
  const micSideCheck = Checking();
  const micMode = createSegmented<MicMode>({
    name: 'micMode',
    legend: 'mic.mode',
    items: [
      { value: 'standard', label: 'mic.standard' },
      { value: 'voiceIsolation', label: 'mic.voiceIsolation' },
      { value: 'wideSpectrum', label: 'mic.wideSpectrum' },
    ],
    onChange: (value) => {
      const d = current();
      if (d) {
        d.host.micMode = value;
        post({ type: 'setHostSetting', id: d.id, name: 'micMode', value });
      }
    },
  });
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
  let featList!: HTMLDivElement;
  const featLocked = LockRow('featLocked');
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
    const row = createSwitchRow({ id: `f-${def.key}`, title: def.title, desc: null, stack: true, checking: true, onChange: (on) => onFeature(def.key, on) });
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
              if (d) {
                post({ type: d.conn === 'connected' ? 'disconnect' : d.conn === 'connecting' ? 'cancel' : 'connect', id: d.id });
              }
            }} />
        </div>
      </section>

      <section class="wp-group" ref={ncGroup}>
        <h2 class="wp-group__title" l:text="nc.title" />
        <div class="wp-list">
          <div class="wp-row wp-row--stack">{ncLocked}{nc.el}{ncOneBud}</div>
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
          {micLocked}
          <div class="wp-row"><div class="wp-row__text"><span class="wp-row__desc" ref={micDesc} /></div></div>
          <div class="wp-row wp-row--stack">
            <div class="wp-row__head"><div class="wp-row__text"><span class="wp-row__title" l:text="mic.mode" /></div></div>
            {micMode.el}
            {Note({ label: 'mic.rnnoiseNote' })}
          </div>
          <div class="wp-row wp-row--stack" ref={micSideRow}>
            <div class="wp-row__head">
              <div class="wp-row__text"><span class="wp-row__title" l:text="mic.side" /><span class="wp-row__desc" l:text="mic.sideDesc" /></div>
              {micSideCheck}
            </div>
            {micSide.el}
          </div>
        </div>
      </section>

      <section class="wp-group" ref={featGroup}>
        <h2 class="wp-group__title" l:text="features.title" />
        <div class="wp-list" ref={featList}>{featLocked}</div>
      </section>

      <section class="wp-group">
        <h2 class="wp-group__title" l:text="home.more" />
        <div class="wp-list">{toControls}{toWindows}</div>
      </section>
    </div>
  ) as HTMLDivElement;
  for (const f of features) {
    featList.append(f.row);
  }

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
    const ear = st ? st.ear : null;
    const any = batteries.update(bat, { left: earText(ear, true), right: earText(ear, false) }, d.art === 'pro' ? 'case-pro' : 'case');
    setHidden(batteries.el, !any);
    setClass(hero, 'wp-hero--stale', conn === 'reconnecting');

    const single = capsOf(d).has('singleBattery');
    const hintKey = conn === 'disconnected' ? (single ? 'home.hintMax' : 'home.hintBuds') : conn === 'elsewhere' ? 'home.hintElsewhere' : null;
    setHidden(hint, !hintKey);
    if (hintKey) {
      setText(hint, t(hintKey));
    }
    setHidden(keysHint, d.hasKeys || conn === 'connected');

    const act = conn === 'connected' ? (['home.disconnect', 'secondary'] as const)
      : conn === 'connecting' ? (['home.cancel', 'secondary'] as const)
        : conn === 'reconnecting' ? null : (['home.connect', 'primary'] as const);
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
    nc.showItem('adaptive', caps.has('adaptive'));
    setHidden(ncLocked, !!st);
    const inEar = st ? Number(st.ear.primary === 'inEar') + Number(st.ear.secondary === 'inEar') : 2;
    const oneIn = !!st && !caps.has('singleBattery') && inEar === 1 && st.oneBudAnc !== true;
    setHidden(ncOneBud, !oneIn);
    for (const mode of ['off', 'noiseCancellation', 'transparency', 'adaptive'] as const) {
      const needsBoth = mode === 'noiseCancellation' || mode === 'adaptive';
      nc.lockItem(mode, !st || (oneIn && needsBoth), !st ? 'ncLocked' : oneIn && needsBoth ? 'ncOneBud' : null);
    }
    nc.set(st?.listeningMode ?? null);
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
    setHidden(micLocked, !!st);
    micMode.set(d.host.micMode);
    micMode.lock(!st, st ? null : 'micLocked');
    setHidden(micSideRow, !capsOf(d).has('micSide'));
    const checking = !!st && st.checking && st.micSide === null;
    setHidden(micSideCheck, !checking);
    setHidden(micSide.el, checking);
    micSide.lock(!st, st ? null : 'micLocked');
    micSide.set(st?.micSide ?? null);
  };

  const renderFeatures = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
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
        reason = st?.hearingAidEnrolled === false;
        value = st && st.hearingAidEnrolled !== null ? st.hearingAidEnabled : null;
      } else {
        value = st ? st[def.key] : null;
      }
      const checking = !!st && st.checking && !reason && value === null;
      setClass(f.row, 'is-disabled', reason);
      setText(f.desc, t(reason ? 'features.hearingAidSetup' : single && def.descMax ? def.descMax : def.desc));
      if (f.check) {
        setHidden(f.check, !checking);
      }
      setHidden(f.sw, checking);
      if (f.input.checked !== (!reason && !!value)) {
        f.input.checked = !reason && !!value;
      }
      setDisabled(f.input, !st || reason);
      setAttr(f.input, 'aria-describedby', !st ? 'featLocked' : reason ? f.desc.id : null);
    }
    setHidden(featGroup, !any);
    setHidden(featLocked, !!st);

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
    returnFocus: (from) => (from === 'controls' ? toControls : from === 'windows' ? toWindows : from === 'pair' ? menu.button : null),
  };
};
