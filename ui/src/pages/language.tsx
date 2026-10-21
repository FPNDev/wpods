/* Language: search, "Same as Windows", then every installed locale. The host answers a pick with `labels`
   and the window switches language in place. */
import { setAttr, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon } from '../icons';
import { store } from '../state';
import type { Ctx, Page } from './page';

type Row = { el: HTMLButtonElement; hay: string };

export const createLanguage = (ctx: Ctx): Page => {
  let search!: HTMLInputElement;
  let auto!: HTMLButtonElement;
  let windowsName!: HTMLSpanElement;
  let list!: HTMLDivElement;
  let none!: HTMLParagraphElement;
  const rows: Row[] = [];
  const byCode = new Map<string, HTMLButtonElement>();
  let checked: HTMLButtonElement | null = null;

  const pick = (code: string): void => {
    if (code !== store.language) {
      store.language = code;
      post({ type: 'setLanguage', code });
      ctx.update('langs', 'windows');
    }
  };
  const filter = (): void => {
    const query = search.value.trim();
    const q = query.toLowerCase();
    let visible = 0;
    for (const row of rows) {
      const show = !q || row.hay.includes(q);
      setHidden(row.el, !show);
      visible += show ? 1 : 0;
    }
    setHidden(none, visible > 0);
    if (visible === 0) {
      setText(none, t('lang.noMatch', { query }));
    }
  };

  const el = (
    <div class="wp-page">
      <div class="wp-search">
        <Icon name="search" class="wp-search__icon" />
        <input class="wp-search__input" type="search" l:aria-label="lang.search" l:placeholder="lang.search" ref={search}
          oninput={filter}
          onkeydown={(e) => {
            if (e.key === 'Escape' && search.value) {
              e.stopPropagation();
            }
          }} />
      </div>
      <section class="wp-group">
        <div class="wp-list" role="radiogroup" l:aria-label="lang.title">
          <button type="button" class="wp-row wp-choice" role="radio" aria-checked="false" ref={auto} onclick={() => pick('')}>
            <span class="wp-row__lead"><Icon name="globe" /></span>
            <span class="wp-row__text"><span class="wp-row__title" l:text="lang.sameAsWindows" /><span class="wp-row__desc" ref={windowsName} /></span>
            <Icon name="check" class="wp-row__check" />
          </button>
        </div>
      </section>
      <section class="wp-group">
        <h2 class="wp-group__title" l:text="lang.all" />
        <div class="wp-list" role="radiogroup" l:aria-label="lang.all" ref={list}>
          <p class="wp-empty-note" hidden ref={none} />
        </div>
        <p class="wp-group__foot" l:text="lang.foot" />
      </section>
    </div>
  ) as HTMLDivElement;

  for (const l of store.languages.toSorted((a, b) => a.engName.localeCompare(b.engName))) {
    let title!: HTMLSpanElement;
    const row = (
      <button type="button" class="wp-row wp-choice" role="radio" aria-checked="false" onclick={() => pick(l.code)}>
        <span class="wp-row__text"><span class="wp-row__title" ref={title}>{l.originalName}</span><span class="wp-row__desc">{l.engName}</span></span>
        <Icon name="check" class="wp-row__check" />
      </button>
    ) as HTMLButtonElement;
    title.lang = l.code;
    list.insertBefore(row, none);
    rows.push({ el: row, hay: `${l.originalName}\n${l.engName}`.toLowerCase() });
    byCode.set(l.code, row);
  }
  setText(windowsName, store.windowsLanguageName);

  return {
    el,
    sections: {
      langs: () => {
        const next = store.language === '' ? auto : byCode.get(store.language) ?? null;
        if (next !== checked) {
          if (checked) {
            setAttr(checked, 'aria-checked', 'false');
          }
          if (next) {
            setAttr(next, 'aria-checked', 'true');
          }
          checked = next;
        }
        if (!none.hidden) {
          filter();
        }
      },
    },
    leave: () => {
      search.value = '';
      filter();
    },
  };
};
