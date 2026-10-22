/* Host <-> page messages (scratchpad ui-protocol.md, v3). Every message is one JSON object with a string `type`. */

export type DeviceId = string;
export type Battery = { pct: number; charging: boolean } | null;
export type Batteries = { left: Battery; right: Battery; case: Battery; headphone: Battery };
export type Ear = 'inEar' | 'out' | 'inCase' | 'disconnected' | null;
export type EarState = { left: Ear; right: Ear };
export type Art = 'pro' | 'airpods4' | 'airpods' | 'max';
export type Capability = 'listeningMode' | 'adaptive' | 'earDetection' | 'conversationAwareness' | 'oneBudAnc'
  | 'personalizedVolume' | 'allowOff' | 'micSide' | 'pressSpeed' | 'holdDuration' | 'toneVolume' | 'hearingAid' | 'heartRate'
  | 'singleBattery';
export type Conn = 'connected' | 'connecting' | 'reconnecting' | 'notConnected' | 'elsewhere' | 'attachFailed';
export type MicMode = 'standard' | 'voiceIsolation' | 'wideSpectrum';
type HostSettings = { disableHandsFree: boolean; lowLatency: boolean; micMode: MicMode; duckPercent: number };
export type StemMode = 'off' | 'noiseCancellation' | 'transparency' | 'adaptive';
export type Device = {
  id: DeviceId;
  name: string;
  model: string;
  art: Art;
  caps: Capability[];
  conn: Conn;
  hasKeys: boolean;
  batteries: Batteries | null;
  host: HostSettings;
  stemModes: StemMode[];
};
export type ListeningMode = 'off' | 'noiseCancellation' | 'transparency' | 'adaptive';
export type MicSide = 'automatic' | 'left' | 'right';
export type PressSpeed = 'default' | 'slower' | 'slowest';
export type HoldDuration = 'default' | 'shorter' | 'shortest';
export type HeartRate = 'off' | 'starting' | 'streaming' | 'noReading' | 'failed';
type Settings = {
  earDetection: boolean | null;
  conversationAwareness: boolean | null;
  personalizedVolume: boolean | null;
  oneBudAnc: boolean | null;
  micSide: MicSide | null;
  pressSpeed: PressSpeed | null;
  holdDuration: HoldDuration | null;
  toneVolume: number | null;
  hearingAidEnrolled: boolean | null;
  hearingAidEnabled: boolean;
};
export type State = Settings & {
  battery: Batteries;
  ear: EarState;
  listeningMode: ListeningMode | null;
  heartRate: HeartRate;
  heartRateBpm: number | null;
  caps: Capability[];
  checking: boolean;
};
export type ToggleName = 'earDetection' | 'conversationAwareness' | 'personalizedVolume' | 'oneBudAnc';
export type Theme = 'light' | 'dark' | 'auto';
export type AccentRamp = { accent: string; dark1: string; dark2: string; dark3: string; light1: string; light2: string; light3: string };
export type Language = { code: string; originalName: string; engName: string };
export type MicStatus = 'notActive' | 'starting' | 'live' | 'problem';
export type ToastId = 'noResponse' | 'connectFailed' | 'bluetoothOff' | 'micFailed' | 'lowLatencyFailed' | 'handsFreeFailed' | 'disconnectFailed';
type OfferState = 'offer' | 'connecting' | 'connected' | 'failed';
export type Offer = { id: DeviceId; name: string; art: Art; batteries: Batteries; state: OfferState };

type Labels = { labels: Record<string, string>; lang: string; rtl: boolean };
type Prefs = { theme: Theme; useAccent: boolean; accent: AccentRamp | null; visible: boolean };

/* Messages both pages take. */
type CommonMessage =
  | ({ type: 'labels' } & Labels)
  | { type: 'prefs'; startup?: boolean; theme?: Theme; useAccent?: boolean }
  | { type: 'accent'; accent: AccentRamp | null }
  | { type: 'visibility'; visible: boolean };

type MainInit = { type: 'init' } & Labels & Prefs & {
  languages: Language[];
  language: string;
  windowsLanguageName: string;
  startup: boolean;
  firstRun: boolean;
  devices: Device[];
  attached: DeviceId | null;
  selected: DeviceId | null;
  state: State | null;
  mic: MicStatus;
};

export type HostToMain =
  | CommonMessage
  | MainInit
  | { type: 'devices'; devices: Device[]; attached: DeviceId | null }
  | { type: 'state'; state: State | null }
  | { type: 'battery'; battery: Batteries }
  | { type: 'ear'; ear: EarState }
  | { type: 'listeningMode'; listeningMode: ListeningMode | null }
  | { type: 'settings'; settings: Partial<Settings> }
  | { type: 'heartRate'; heartRate: HeartRate; heartRateBpm: number | null }
  | { type: 'caps'; caps: Capability[] }
  | { type: 'checking'; checking: boolean }
  | { type: 'hostSettings'; id: DeviceId; host: HostSettings }
  | { type: 'mic'; status: MicStatus }
  | { type: 'toast'; toast: ToastId; device: DeviceId | null };

export type HostToPopup =
  | CommonMessage
  | ({ type: 'init' } & Labels & Prefs & { offers: Offer[] })
  | { type: 'offers'; offers: Offer[] }
  | { type: 'focus' };

export type MainToHost =
  | { type: 'ready'; page: 'main' }
  | { type: 'connect' | 'cancel' | 'disconnect' | 'reconnect'; id: DeviceId }
  | { type: 'setListeningMode'; mode: ListeningMode }
  | { type: 'setToggle'; name: ToggleName; value: boolean }
  | { type: 'setHeartRate' | 'setHearingAid' | 'setAccent' | 'setStartup'; value: boolean }
  | { type: 'setMicSide'; side: MicSide }
  | { type: 'setPressSpeed'; value: PressSpeed }
  | { type: 'setHoldDuration'; value: HoldDuration }
  | { type: 'setToneVolume'; value: number }
  | { type: 'setHostSetting'; id: DeviceId; name: 'disableHandsFree' | 'lowLatency'; value: boolean }
  | { type: 'setHostSetting'; id: DeviceId; name: 'micMode'; value: MicMode }
  | { type: 'setHostSetting'; id: DeviceId; name: 'duckPercent'; value: number }
  | { type: 'setStemModes'; id: DeviceId; modes: StemMode[] }
  | { type: 'setTheme'; theme: Theme }
  | { type: 'setLanguage'; code: string }
  | { type: 'openBluetoothSettings' | 'openAddDevice' | 'getStarted' | 'minimize' | 'close' | 'beginDrag' };

export type PopupToHost =
  | { type: 'ready'; page: 'popup' }
  | { type: 'popupConnect'; id: DeviceId }
  | { type: 'popupCancel' }
  | { type: 'popupDismiss'; ids: DeviceId[] }
  | { type: 'popupSize'; height: number };
