import { applyCommon } from "./common";
import { createToast } from "./components/toast";
import { createAnnouncer, createFrame, setHidden, setText } from "./dom";
import { listen, post } from "./host";
import { t, type LabelKey } from "./i18n";
import { Glyph, Icon } from "./icons";
import { createControls } from "./pages/controls";
import { createEmpty } from "./pages/empty";
import { createEnhancement } from "./pages/enhancement";
import { createFirstRun } from "./pages/firstRun";
import { createHearing } from "./pages/hearing";
import { createHome } from "./pages/home";
import { createLanguage } from "./pages/language";
import { createLegal } from "./pages/legal";
import { createMicModes } from "./pages/micModes";
import type { Ctx, Page, PageName } from "./pages/page";
import { createPairGuide } from "./pages/pairGuide";
import { createWindowsSettings } from "./pages/windowsSettings";
import type { HostToMain } from "./protocol";
import { apply, current, dirty, mark, type Section, store } from "./state";

type Route = {
  create: (ctx: Ctx) => Page;
  title: LabelKey | null;
  parent: PageName | null;
};
type Built = { page: Page; renders: [Section, () => void][] };

const ROUTES: Record<PageName, Route> = {
  firstRun: { create: createFirstRun, title: null, parent: null },
  empty: { create: createEmpty, title: null, parent: null },
  home: { create: createHome, title: null, parent: null },
  controls: { create: createControls, title: "controls.title", parent: "home" },
  hearing: { create: createHearing, title: "hearing.title", parent: "home" },
  windows: {
    create: createWindowsSettings,
    title: "windows.title",
    parent: "home",
  },
  language: { create: createLanguage, title: "lang.title", parent: "windows" },
  pair: { create: createPairGuide, title: "pair.title", parent: "home" },
  enhancement: {
    create: createEnhancement,
    title: "mic.enhancement",
    parent: "home",
  },
  micModes: { create: createMicModes, title: "mic.modes", parent: "windows" },
  legal: { create: createLegal, title: "legal.title", parent: "home" },
};

const built = new Map<PageName, Built>();
let requested: PageName = "home";
let shown: PageName | null = null;
let navDir = 0;

const schedule = createFrame(() => render());
const ctx: Ctx = {
  go: (page) => {
    requested = page;
    navDir = 1;
    ctx.update("page");
  },
  update: (...sections) => {
    mark(...sections);
    schedule();
  },
};
const back = (): void => {
  if (!shown) {
    return;
  }
  built.get(shown)?.page.leave?.();
  requested = ROUTES[shown].parent ?? "home";
  navDir = -1;
  ctx.update("page");
};

let backBtn!: HTMLButtonElement;
let pageTitle!: HTMLSpanElement;
let appTitle!: HTMLDivElement;
let body!: HTMLDivElement;
let announce!: HTMLDivElement;
const toast = createToast((action) => {
  if (action === "openSettings") {
    post({ type: "openBluetoothSettings" });
  } else {
    const id = store.toastDevice ?? store.selected;
    if (id !== null) {
      post({ type: action === "tryAgain" ? "connect" : "reconnect", id });
    }
  }
});
const noDrag = (e: MouseEvent): void => e.stopPropagation();
const shell = (
  <div class="wp-window">
    <div
      class="wp-titlebar"
      onmousedown={(e) => {
        if (e.button === 0) {
          e.preventDefault();
          post({ type: "beginDrag" });
        }
      }}
    >
      <button
        type="button"
        class="wp-iconbtn wp-back"
        l:aria-label="common.back"
        hidden
        ref={backBtn}
        onmousedown={noDrag}
        onclick={back}
      >
        <Icon name="chevron-left" />
      </button>
      <span class="wp-titlebar__title" hidden ref={pageTitle} />
      <div class="wp-titlebar__app" ref={appTitle}>
        <Glyph name="pair" />
        <span class="t-caption-strong" l:text="app.name" />
      </div>
      <div class="wp-titlebar__caps">
        <button
          type="button"
          class="wp-iconbtn"
          l:aria-label="window.minimize"
          onmousedown={noDrag}
          onclick={() => post({ type: "minimize" })}
        >
          <Icon name="minimize" />
        </button>
        <button
          type="button"
          class="wp-iconbtn wp-iconbtn--close"
          l:aria-label="window.close"
          onmousedown={noDrag}
          onclick={() => post({ type: "close" })}
        >
          <Icon name="close" />
        </button>
      </div>
    </div>
    <div class="wp-body" ref={body} />
    {toast.el}
    <div class="wp-sr" role="status" ref={announce} />
  </div>
);
document.body.append(shell);
const announcer = createAnnouncer(announce);

const pageOf = (name: PageName): Built => {
  let b = built.get(name);
  if (!b) {
    const page = ROUTES[name].create(ctx);
    b = {
      page,
      renders: Object.entries(page.sections) as [Section, () => void][],
    };
    built.set(name, b);
  }
  return b;
};

const renderTitle = (): void => {
  const key = shown ? ROUTES[shown].title : null;
  setHidden(backBtn, !key);
  setHidden(pageTitle, !key);
  setHidden(appTitle, !!key);
  if (key) {
    setText(pageTitle, t(key));
  }
};

const renderPage = (): void => {
  const top: PageName =
    requested === "legal"
      ? "legal"
      : store.firstRun
        ? "firstRun"
        : store.devices.length === 0
          ? "empty"
          : requested;
  if (top === "firstRun" || top === "empty") {
    requested = "home";
  }
  if (top === shown) {
    return;
  }
  const left = shown;
  const b = pageOf(top);
  const el = b.page.el;
  el.classList.remove("wp-page-enter", "wp-page-back");
  if (left && navDir) {
    el.classList.add(navDir > 0 ? "wp-page-enter" : "wp-page-back");
  }
  body.replaceChildren(el);
  body.scrollTop = 0;
  shown = top;
  renderTitle();
  for (const [section, run] of b.renders) {
    run();
    dirty.delete(section);
  }
  if (navDir > 0 && ROUTES[top].title) {
    backBtn.focus();
  } else if (navDir < 0 && left) {
    b.page.returnFocus?.(left)?.focus();
  }
  navDir = 0;
};

const render = (): void => {
  if (store.announce) {
    const text = store.announce;
    store.announce = "";
    announcer(text);
  }
  if (store.toast) {
    toast.show(store.toast, store.toastCode);
    shell.insertBefore(toast.el, announce);
    store.toast = null;
  }
  if (store.relabel) {
    store.relabel = false;
    toast.relabel();
  }
  if (!store.ready) {
    return;
  }
  if (dirty.has("page")) {
    renderPage();
  }
  if (dirty.has("title")) {
    renderTitle();
  }
  if (shown && (current() || shown === "firstRun" || shown === "empty")) {
    for (const [section, run] of pageOf(shown).renders) {
      if (dirty.has(section)) {
        run();
      }
    }
  }
  dirty.clear();
};

listen<HostToMain>((msg) => {
  applyCommon(msg);
  apply(msg);
  schedule();
});

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && shown && ROUTES[shown].parent) {
    back();
  }
});

post({ type: "ready", page: "main" });
