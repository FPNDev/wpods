/* Main window store: the host's data as last received, plus which render sections it made stale. Messages are
   applied as they arrive; main.tsx renders the stale sections of the shown page once per animation frame. */
import { t } from './i18n';
import type { Batteries, Capability, Conn, Device, DeviceId, HostToMain, Language, MicCatalog, MicConfig, MicMode, MicSlot, MicStatus, State, ToastId } from './protocol';

export type Section = 'page' | 'title' | 'hero' | 'menu' | 'nc' | 'mic' | 'features' | 'controls' | 'windows' | 'langs' | 'firstRun';
type Entry = Device & { capSet: Set<Capability> };

const BATTERY_PARTS = { left: 'left', right: 'right', case: 'case', headphone: 'max' } as const;
const DEVICE_SECTIONS: Section[] = ['page', 'hero', 'menu', 'nc', 'mic', 'features', 'controls', 'windows'];
const ALL_SECTIONS: Section[] = [...DEVICE_SECTIONS, 'title', 'langs', 'firstRun'];

export const store = {
  ready: false,
  devices: [] as Entry[],
  byId: new Map<DeviceId, Entry>(),
  attached: null as DeviceId | null,
  selected: null as DeviceId | null,
  state: null as State | null,
  stateCaps: new Set<Capability>(),
  mic: 'notActive' as MicStatus,
  startup: false,
  firstRun: false,
  language: '',
  languages: [] as Language[],
  languageByCode: new Map<string, Language>(),
  windowsLanguageName: '',
  /* The model lists (sent once in init) and the app-wide mic config. */
  micCatalog: { standard: [], voiceIsolation: [], wideSpectrum: [], enhancement: [] } as MicCatalog,
  micConfig: { modes: [], models: { standard: '', voiceIsolation: '', wideSpectrum: '' }, enhancement: null } as MicConfig,
  micEnabled: new Set<MicMode>(),
  /* Per slot, the id whose stage the attached pair loads now, or null. */
  micLoading: { mode: null, enhancement: null } as Record<MicSlot, string | null>,
  lastBattery: new Map<DeviceId, Batteries>(),
  lowSaid: new Set<string>(),
  /* One-shot outputs, consumed by the next frame. */
  announce: '',
  toast: null as ToastId | null,
  toastDevice: null as DeviceId | null,
  toastCode: null as string | null,
  relabel: false,
};

export const dirty = new Set<Section>();
export const mark = (...sections: Section[]): void => {
  for (const s of sections) {
    dirty.add(s);
  }
};

/* Fallback cycle of the effective mic mode: voiceIsolation -> standard -> wideSpectrum -> (wraps). */
const NEXT_MODE: Record<MicMode, MicMode> = { voiceIsolation: 'standard', standard: 'wideSpectrum', wideSpectrum: 'voiceIsolation' };
/* The saved mode if enabled, else the first enabled one walking the cycle forward after it. The saved mode itself is
   never changed, so re-enabling it brings it back. */
export const effectiveMode = (saved: MicMode): MicMode => {
  let mode = saved;
  for (let i = 0; i < 3 && !store.micEnabled.has(mode); i++) {
    mode = NEXT_MODE[mode];
  }
  return mode;
};
/* Takes a mic config (from the host, or the page's optimistic change). */
export const applyMicConfig = (config: MicConfig): void => {
  store.micConfig = config;
  store.micEnabled = new Set(config.modes);
  mark('mic');
};

export const current = (): Entry | null => (store.selected === null ? null : store.byId.get(store.selected) ?? null);
/* The attached pair's live state, or null for any other pair. */
export const live = (d: Entry): State | null => (d.conn === 'connected' && d.id === store.attached ? store.state : null);
/* Capabilities of a pair: the attached session's (they grow as the AirPods report) or the remembered ones. */
export const capsOf = (d: Entry): Set<Capability> => (live(d) ? store.stateCaps : d.capSet);

/* Pairs the user just disconnected: shown Not connected at once (optimistic) until the host reports a conn that
   isn't connected / reconnecting / connecting, or a disconnectFailed toast reverts to the host's real value. */
const disconnecting = new Set<DeviceId>();
const MASKED = new Set<Conn>(['connected', 'reconnecting', 'connecting']);
let lastDevices: Device[] = [];

export const beginDisconnect = (id: DeviceId): void => {
  disconnecting.add(id);
  const d = store.byId.get(id);
  if (d) {
    d.conn = 'notConnected';
  }
};

const applyDevices = (devices: Device[], attached: DeviceId | null): void => {
  lastDevices = devices;
  store.devices = [];
  store.byId.clear();
  for (const d of devices) {
    const entry = { ...d, capSet: new Set(d.caps) };
    if (disconnecting.has(d.id)) {
      if (MASKED.has(d.conn)) {
        entry.conn = 'notConnected';
      } else {
        disconnecting.delete(d.id);
      }
    }
    store.devices.push(entry);
    store.byId.set(d.id, entry);
  }
  if (attached !== store.attached) {
    store.attached = attached;
    store.lowSaid.clear();
    if (attached) {
      store.selected = attached;
    }
  }
  if (store.selected === null || !store.byId.has(store.selected)) {
    store.selected = store.attached ?? devices[0]?.id ?? null;
  }
  mark(...DEVICE_SECTIONS);
};

const applyBattery = (battery: Batteries): void => {
  if (!store.state || store.attached === null) {
    return;
  }
  store.state.battery = battery;
  store.lastBattery.set(store.attached, battery);
  const name = store.byId.get(store.attached)?.name ?? '';
  let said = '';
  for (const [part, ring] of Object.entries(BATTERY_PARTS)) {
    const b = battery[part as keyof Batteries];
    if (!b) {
      continue;
    }
    if (b.charging || b.pct >= 10) {
      store.lowSaid.delete(part);
    } else if (!store.lowSaid.has(part)) {
      store.lowSaid.add(part);
      said += (said ? '. ' : '') + t('battery.low', { name, part: t(`battery.${ring}`), pct: b.pct });
    }
  }
  if (said) {
    store.announce = said;
  }
  mark('hero');
};

const applyState = (state: State | null): void => {
  store.state = state;
  store.stateCaps = new Set(state?.caps);
  /* lowSaid is kept: a new snapshot for the same attached pair (re-attach) never repeats the low-battery announce;
     applyDevices clears it when the attached pair changes. */
  if (state) {
    applyBattery(state.battery);
  }
  mark('hero', 'nc', 'mic', 'features', 'controls', 'windows');
};

export const apply = (msg: HostToMain): void => {
  const st = store.state;
  switch (msg.type) {
    case 'init':
      store.startup = msg.startup;
      store.firstRun = msg.firstRun;
      store.language = msg.language;
      store.languages = msg.languages;
      store.languageByCode.clear();
      for (const l of msg.languages) {
        store.languageByCode.set(l.code, l);
      }
      store.windowsLanguageName = msg.windowsLanguageName;
      store.micCatalog = msg.micCatalog;
      applyMicConfig(msg.micConfig);
      store.micLoading.mode = null;
      store.micLoading.enhancement = null;
      store.mic = msg.mic;
      store.attached = msg.attached;
      store.selected = msg.selected ?? msg.attached;
      applyDevices(msg.devices, msg.attached);
      applyState(msg.state);
      store.ready = true;
      mark(...ALL_SECTIONS);
      break;
    case 'labels':
      store.relabel = true;
      mark(...ALL_SECTIONS);
      break;
    case 'devices':
      applyDevices(msg.devices, msg.attached);
      break;
    case 'state':
      applyState(msg.state);
      break;
    case 'battery':
      applyBattery(msg.battery);
      break;
    case 'ear':
      if (st) {
        st.ear = msg.ear;
        mark('hero', 'nc');
      }
      break;
    case 'listeningMode':
      if (st) {
        st.listeningMode = msg.listeningMode;
        mark('nc');
      }
      break;
    case 'settings':
      if (st) {
        Object.assign(st, msg.settings);
        mark('nc', 'mic', 'features', 'controls');
      }
      break;
    case 'heartRate':
      if (st) {
        st.heartRate = msg.heartRate;
        st.heartRateBpm = msg.heartRateBpm;
        mark('features');
      }
      break;
    case 'caps':
      if (st) {
        st.caps = msg.caps;
        store.stateCaps = new Set(msg.caps);
        mark('hero', 'nc', 'mic', 'features', 'controls');
      }
      break;
    case 'checking':
      if (st) {
        st.checking = msg.checking;
        st.ready = msg.ready;
        mark('nc', 'mic', 'features', 'controls');
      }
      break;
    case 'hostSettings': {
      const d = store.byId.get(msg.id);
      if (d) {
        d.host = msg.host;
        mark('mic', 'windows', 'controls');
      }
      break;
    }
    case 'mic':
      store.mic = msg.status;
      mark('mic');
      break;
    case 'micConfig':
      applyMicConfig(msg.config);
      break;
    case 'micStage':
      store.micLoading[msg.slot] = msg.state === 'loading' ? msg.id : null;
      mark('mic');
      break;
    case 'prefs':
      if (msg.startup !== undefined) {
        store.startup = msg.startup;
      }
      mark('windows', 'firstRun');
      break;
    case 'toast':
      store.toast = msg.toast;
      store.toastDevice = msg.device;
      store.toastCode = msg.code ?? null;
      if (msg.toast === 'disconnectFailed' && msg.device !== null && disconnecting.delete(msg.device)) {
        applyDevices(lastDevices, store.attached);
      }
      break;
    default:
      break;
  }
};
