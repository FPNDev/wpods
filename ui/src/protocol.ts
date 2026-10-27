export type DeviceId = string;
export type Battery = { pct: number; charging: boolean } | null;
export type Batteries = {
  left: Battery;
  right: Battery;
  case: Battery;
  headphone: Battery;
};
export type Ear = "inEar" | "out" | "inCase" | "disconnected" | null;
export type EarState = { left: Ear; right: Ear };
export type Art = "pro" | "airpods4" | "airpods" | "max";
export type Capability =
  | "listeningMode"
  | "adaptive"
  | "earDetection"
  | "conversationAwareness"
  | "oneBudAnc"
  | "personalizedVolume"
  | "allowOff"
  | "micSide"
  | "pressSpeed"
  | "holdDuration"
  | "toneVolume"
  | "hearingAid"
  | "mediaAssist"
  | "heartRate"
  | "singleBattery";
export type Conn =
  | "connected"
  | "connecting"
  | "reconnecting"
  | "notConnected"
  | "elsewhere"
  | "attachFailed";
export type MicMode = "standard" | "voiceIsolation" | "wideSpectrum";
type HostSettings = {
  disableHandsFree: boolean;
  lowLatency: boolean;
  micMode: MicMode;
  duckPercent: number;
};
export type MicSlot = "mode" | "enhancement";
type StageLoad = "loading" | "ready" | "failed";
export type Weight = "light" | "medium" | "heavy" | "veryHeavy";
export type MicOption = {
  id: string;
  name: string;
  weight: Weight;
  threads: number;
  mmacs: number;
  latencyMs: number;
};
export type MicCatalog = {
  standard: MicOption[];
  voiceIsolation: MicOption[];
  wideSpectrum: MicOption[];
  enhancement: MicOption[];
};
export type MicConfig = {
  modes: MicMode[];
  models: Record<MicMode, string>;
  enhancement: string | null;
};
export type StemMode =
  "off" | "noiseCancellation" | "transparency" | "adaptive";
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
  saved: Saved;
};
export type ListeningMode =
  "off" | "noiseCancellation" | "transparency" | "adaptive";
export type MicSide = "automatic" | "left" | "right";
export type PressSpeed = "default" | "slower" | "slowest";
export type HoldDuration = "default" | "shorter" | "shortest";
export type HeartRate =
  "off" | "starting" | "streaming" | "noReading" | "failed";
type Settings = {
  earDetection: boolean | null;
  conversationAwareness: boolean | null;
  personalizedVolume: boolean | null;
  oneBudAnc: boolean | null;
  allowOff: boolean | null;
  micSide: MicSide | null;
  pressSpeed: PressSpeed | null;
  holdDuration: HoldDuration | null;
  toneVolume: number | null;
  hearingAidEnrolled: boolean | null;
  hearingAidEnabled: boolean;
  mediaAssistCalls: boolean | null;
  mediaAssistMedia: boolean | null;
};
type Saved = Settings & { listeningMode: ListeningMode | null };
export type State = Settings & {
  battery: Batteries;
  ear: EarState;
  listeningMode: ListeningMode | null;
  heartRate: HeartRate;
  heartRateBpm: number | null;
  caps: Capability[];
  checking: boolean;
  ready: boolean;
};
export type ToggleName =
  | "earDetection"
  | "conversationAwareness"
  | "personalizedVolume"
  | "oneBudAnc"
  | "allowOff";
export type Theme = "light" | "dark" | "auto";
export type AccentRamp = {
  accent: string;
  dark1: string;
  dark2: string;
  dark3: string;
  light1: string;
  light2: string;
  light3: string;
};
export type Language = { code: string; originalName: string; engName: string };
export type MicStatus = "notActive" | "starting" | "live" | "problem";
export type ToastId =
  | "noResponse"
  | "connectFailed"
  | "bluetoothOff"
  | "micFailed"
  | "lowLatencyFailed"
  | "handsFreeFailed"
  | "disconnectFailed"
  | "micModelFailed"
  | "micModeStopped"
  | "settingsReset";
type OfferState = "offer" | "connecting" | "connected" | "failed";
export type Offer = {
  id: DeviceId;
  name: string;
  art: Art;
  batteries: Batteries;
  state: OfferState;
};

type Labels = { labels: Record<string, string>; lang: string; rtl: boolean };
type Prefs = {
  theme: Theme;
  useAccent: boolean;
  accent: AccentRamp | null;
  visible: boolean;
};

type CommonMessage =
  | ({ type: "labels" } & Labels)
  | { type: "prefs"; startup?: boolean; theme?: Theme; useAccent?: boolean }
  | { type: "accent"; accent: AccentRamp | null }
  | { type: "visibility"; visible: boolean };

type MainInit = { type: "init" } & Labels &
  Prefs & {
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
    micCatalog: MicCatalog;
    micConfig: MicConfig;
  };

export type HostToMain =
  | CommonMessage
  | MainInit
  | { type: "devices"; devices: Device[]; attached: DeviceId | null }
  | { type: "state"; state: State | null }
  | { type: "battery"; battery: Batteries }
  | { type: "ear"; ear: EarState }
  | { type: "listeningMode"; listeningMode: ListeningMode | null }
  | { type: "settings"; settings: Partial<Settings> }
  | { type: "heartRate"; heartRate: HeartRate; heartRateBpm: number | null }
  | { type: "caps"; caps: Capability[] }
  | { type: "checking"; checking: boolean; ready: boolean }
  | {
      type: "hostSettings";
      id: DeviceId;
      host: HostSettings;
      handsFreeApplying: boolean;
    }
  | { type: "mic"; status: MicStatus }
  | { type: "micConfig"; config: MicConfig }
  | { type: "micStage"; slot: MicSlot; id: string | null; state: StageLoad }
  | { type: "toast"; toast: ToastId; device: DeviceId | null; code?: string };

export type HostToPopup =
  | CommonMessage
  | ({ type: "init" } & Labels & Prefs & { offers: Offer[] })
  | { type: "offers"; offers: Offer[] }
  | { type: "focus" };

export type MainToHost =
  | { type: "ready"; page: "main" }
  | { type: "connect" | "cancel" | "disconnect" | "reconnect"; id: DeviceId }
  | { type: "setListeningMode"; mode: ListeningMode }
  | { type: "setToggle"; name: ToggleName; value: boolean }
  | {
      type: "setHeartRate" | "setHearingAid" | "setAccent" | "setStartup";
      value: boolean;
    }
  | { type: "setMediaAssist"; name: "calls" | "media"; value: boolean }
  | { type: "setMicSide"; side: MicSide }
  | { type: "setPressSpeed"; value: PressSpeed }
  | { type: "setHoldDuration"; value: HoldDuration }
  | { type: "setToneVolume"; value: number }
  | {
      type: "setHostSetting";
      id: DeviceId;
      name: "disableHandsFree" | "lowLatency";
      value: boolean;
    }
  | { type: "setHostSetting"; id: DeviceId; name: "micMode"; value: MicMode }
  | { type: "setHostSetting"; id: DeviceId; name: "duckPercent"; value: number }
  | { type: "setStemModes"; id: DeviceId; modes: StemMode[] }
  | { type: "setMicModes"; modes: MicMode[] }
  | { type: "setMicModel"; mode: MicMode; name: string }
  | { type: "setEnhancement"; value: string | null }
  | { type: "setTheme"; theme: Theme }
  | { type: "setLanguage"; code: string }
  | { type: "openLegal"; name: "license" | "notices" }
  | {
      type:
        | "openBluetoothSettings"
        | "openAddDevice"
        | "getStarted"
        | "minimize"
        | "close"
        | "beginDrag";
    };

export type PopupToHost =
  | { type: "ready"; page: "popup" }
  | { type: "popupConnect"; id: DeviceId }
  | { type: "popupCancel" }
  | { type: "popupDismiss"; ids: DeviceId[] }
  | { type: "popupSize"; height: number };
