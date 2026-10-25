/* What every page module gives the router in main.tsx. Pages are built on first show and kept (detached) with
   their element handles while another page shows: a re-show is one append, and their bound labels stay current. */
import type { Section } from '../state';

export type PageName = 'firstRun' | 'empty' | 'home' | 'controls' | 'windows' | 'language' | 'pair' | 'enhancement' | 'micModes' | 'legal';

export type Page = {
  el: HTMLDivElement;
  /* Renders per store section; the router runs the stale ones while the page shows (all of them on show). */
  sections: Partial<Record<Section, () => void>>;
  /* Element to focus when coming back from the sub-page `from`. */
  returnFocus?: (from: PageName) => HTMLElement | null;
  /* Called when the page is left by going back. */
  leave?: () => void;
};

export type Ctx = {
  go: (page: PageName) => void;
  /* Marks sections stale and schedules a frame. */
  update: (...sections: Section[]) => void;
};
