# jsx-dom: build-time JSX to DOM

`jsx-dom.mjs` is a dev-only esbuild plugin. Every `.tsx` file goes through a TypeScript custom transformer
(`ts.transform`) that replaces each JSX tree with straight-line DOM code, then esbuild bundles the result as
plain TypeScript. Nothing of it ships: no runtime, no `h()`, no template strings parsed at run time.

`npm run build` (build.mjs) type-checks with `tsc --noEmit` (JSX types: `src/jsx.d.ts`), bundles, and fails if
the output contains `querySelector`, `getElementById`, `getElementsBy*`, `innerHTML` or `outerHTML`.

## Rules

| Write                                                | Becomes                                                                                                                                          |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `<div>...</div>` (lowercase tag)                     | `document.createElement("div")`; SVG tags (`svg`, `g`, `path`, `circle`, `ellipse`, `rect`, `line`, `polyline`, `polygon`) use `createElementNS` |
| `class="a"` / `class={expr}`                         | `el.className = ...` (SVG: `setAttribute("class", ...)`)                                                                                         |
| `name="v"` / `name={expr}` / bare `name`             | `el.setAttribute("name", ...)` (bare = `""`)                                                                                                     |
| `onclick={fn}` (lowercase DOM event name after `on`) | `el.addEventListener("click", fn)`                                                                                                               |
| `ref={target}`                                       | `target = el` (any assignable expression; declare `let target!: HTMLSpanElement`)                                                                |
| `l:text={key}`                                       | `el.append(bindText(key))`: a text node bound to a label key                                                                                     |
| `l:aria-label={key}`, `l:placeholder={key}`          | `bindAttr(el, "aria-label", key)`                                                                                                                |
| text children                                        | one text child: `el.textContent = "..."`; mixed: one `el.append(...)` call                                                                       |
| `{expr}` child                                       | `el.append(expr)` (a Node or a string; never null/arrays)                                                                                        |
| `<Comp a="1" />` (capitalized)                       | `Comp({ a: "1" })`, the returned element is appended; no children, no `ref`, no `l:`                                                             |

- A JSX expression evaluates to its root element (typed `Element`; cast the root, e.g. `<div /> as HTMLDivElement`,
  or use `ref` for typed handles).
- `const x = <.../>`, `x = <.../>`, `return <.../>` and `() => <.../>` are inlined into the surrounding statements;
  any other position becomes an arrow IIFE.
- `bindText` / `bindAttr` are imported automatically from `src/i18n.ts` when `l:` attributes are used. Bound nodes
  are rewritten in place when the host sends new labels (live language switch). Bind only nodes that live as long
  as the page; texts of rebuilt items are set by the renders.
- Not supported (build error): fragments, spread attributes, spread children, HTML entities in JSX text.
- JSX whitespace follows React: lines are trimmed and blank lines dropped.

## Example

```tsx
let title!: HTMLSpanElement;
const row = (
  <button type="button" class="wp-row" onclick={onPick}>
    <span class="wp-row__title" ref={title} />
    <span class="wp-row__desc" l:text="lang.foot" />
    <Icon name="check" />
  </button>
);
```

```ts
import { bindText as __lText } from "./i18n";
let title!: HTMLSpanElement;
const e_1 = document.createElement("button");
e_1.setAttribute("type", "button");
e_1.className = "wp-row";
e_1.addEventListener("click", onPick);
const e_2 = document.createElement("span");
e_2.className = "wp-row__title";
title = e_2;
const e_3 = document.createElement("span");
e_3.className = "wp-row__desc";
e_3.append(__lText("lang.foot"));
const e_4 = Icon({ name: "check" });
e_1.append(e_2, e_3, e_4);
const row = e_1;
```
