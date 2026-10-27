import type { Section } from '../state';

export type PageName =
  | 'firstRun'
  | 'empty'
  | 'home'
  | 'controls'
  | 'hearing'
  | 'windows'
  | 'language'
  | 'pair'
  | 'enhancement'
  | 'micModes'
  | 'legal';

export type Page = {
  el: HTMLDivElement;
  sections: Partial<Record<Section, () => void>>;
  returnFocus?: (from: PageName) => HTMLElement | null;
  leave?: () => void;
};

export type Ctx = {
  go: (page: PageName) => void;
  update: (...sections: Section[]) => void;
};
