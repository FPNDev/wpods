/* Design-system drawings as data: 16x16 stroke icons, 24x24 device glyphs, 240x132 model illustrations and the
   spinner. Shapes are built with createElementNS; a bare string is a <path d>. Art classes (a-body, a-metal...)
   bind every fill to an art-* token in bundle.css. */
import type { Art as ArtModel } from './protocol';

type ShapeTag = 'path' | 'circle' | 'ellipse' | 'rect' | 'g';
type Shape = string | [ShapeTag, Record<string, string | number>, Shape[]?];

const SVG = 'http://www.w3.org/2000/svg';

const circle = (cx: number, cy: number, r: number, extra?: Record<string, string>): Shape => ['circle', { cx, cy, r, ...extra }];
const rect = (x: number, y: number, width: number, height: number, rx: number, cls?: string): Shape =>
  ['rect', cls ? { class: cls, x, y, width, height, rx } : { x, y, width, height, rx }];
const ellipse = (cls: string, cx: number, cy: number, rx: number, ry: number, opacity?: number): Shape =>
  ['ellipse', opacity === undefined ? { class: cls, cx, cy, rx, ry } : { class: cls, cx, cy, rx, ry, opacity }];
const line = (cls: string, d: string): Shape => ['path', { class: cls, d }];
const group = (transform: string, children: Shape[]): Shape => ['g', { transform }, children];
const FILL = { fill: 'currentColor' };

const ICONS = {
  'chevron-down': ['M4 6l4 4 4-4'],
  'chevron-right': ['M6 4l4 4-4 4'],
  'chevron-left': ['M10 4L6 8l4 4'],
  'close': ['M4 4l8 8M12 4l-8 8'],
  'minimize': ['M3.5 8h9'],
  'check': ['M3.5 8.5l3 3 6-7'],
  'plus': ['M8 3v10M3 8h10'],
  'bolt': ['M9.2 1L3.4 9h4.1l-.9 6 5.9-8.1H8.4z'],
  'heart': ['M8 14S2.25 10.6 2.25 6.6A3.1 3.1 0 0 1 8 4.9a3.1 3.1 0 0 1 5.75 1.7C13.75 10.6 8 14 8 14z'],
  'info': [circle(8, 8, 6.25), 'M8 7.25v4M8 4.9v.1'],
  'warning': ['M7.13 2.6a1 1 0 0 1 1.74 0l5.4 9.4a1 1 0 0 1-.87 1.5H2.6a1 1 0 0 1-.87-1.5z', 'M8 6.25v3M8 11.25v.1'],
  'error': [circle(8, 8, 6.25), 'M5.9 5.9l4.2 4.2M10.1 5.9l-4.2 4.2'],
  'mic': [rect(5.75, 1.75, 4.5, 8, 2.25), 'M3.25 7.75a4.75 4.75 0 0 0 9.5 0M8 12.5v1.75'],
  'pc': [rect(1.75, 2.75, 12.5, 8.5, 1.5), 'M5.5 13.75h5M8 11.25v2.5'],
  'press': [rect(5.75, 6.75, 4.5, 7.5, 2.25), 'M8 1.75v2.5M4.25 3.25l1.25 1.25M11.75 3.25L10.5 4.5'],
  'external': ['M9.5 2.25h4.25V6.5M13.75 2.25L8 8M12 9.75V12.5a1.25 1.25 0 0 1-1.25 1.25H3.5a1.25 1.25 0 0 1-1.25-1.25V5.25A1.25 1.25 0 0 1 3.5 4h2.75'],
  'bluetooth': ['M4.5 5.25l7 5.5L8 13.75V2.25l3.5 3-7 5.5'],
  'power': ['M8 1.75v5.5', 'M4.6 4.1a5.25 5.25 0 1 0 6.8 0'],
  'double': [circle(5.25, 8, 2.25), circle(10.75, 8, 2.25)],
  'hold': [circle(8, 8, 2.25), 'M8 1.75a6.25 6.25 0 1 1-6.25 6.25'],
  'search': [circle(7, 7, 4.75), 'M10.5 10.5l3.75 3.75'],
  'globe': [circle(8, 8, 6.25), 'M1.75 8h12.5M8 1.75c1.9 1.7 2.85 3.8 2.85 6.25S9.9 12.55 8 14.25C6.1 12.55 5.15 10.45 5.15 8S6.1 3.45 8 1.75z'],
  'tone': ['M5.25 12V3.5l7-1.5V10.5', circle(3.75, 12, 1.75), circle(10.75, 10.5, 1.75)],
  'nc-off': [circle(8, 8, 5.75), 'M4 12L12 4'],
  'nc-anc': [circle(8, 8, 5.75), circle(8, 8, 2.75, FILL)],
  'nc-transparency': [circle(8, 8, 5.75, { 'stroke-dasharray': '1.6 2' }), circle(8, 8, 1.5, FILL)],
  'nc-adaptive': [circle(8, 8, 5.75), ['path', { d: 'M8 2.25a5.75 5.75 0 0 1 0 11.5z', ...FILL }]],
} satisfies Record<string, Shape[]>;
export type IconName = keyof typeof ICONS;

const BUD_L = 'M12 12.03A4.5 4.5 0 1 1 14.5 8V19a1.25 1.25 0 0 1-2.5 0Z';
const BUD_R = 'M12 12.03A4.5 4.5 0 1 0 9.5 8V19a1.25 1.25 0 0 0 2.5 0Z';
const GLYPHS = {
  'bud-left': [BUD_L],
  'bud-right': [BUD_R],
  'case-pro': [rect(3.5, 6, 17, 13, 4), 'M3.5 10.5h17M11.25 15h1.5'],
  'case': [rect(6, 3.5, 12, 17, 4), 'M6 8.5h12M11.5 14.5h1'],
  'max': ['M5.5 13v-2a6.5 6.5 0 0 1 13 0v2', rect(3.75, 12.25, 4.5, 7.5, 2), rect(15.75, 12.25, 4.5, 7.5, 2)],
  'pair': [group('translate(-4.5 0)', [BUD_L]), group('translate(4.5 0)', [BUD_R])],
} satisfies Record<string, Shape[]>;
type GlyphName = keyof typeof GLYPHS;

const proBud: Shape[] = [
  group('translate(48 60) rotate(6)', [
    rect(-1, 4, 13, 46, 6.5, 'a-body'),
    rect(-1, 39, 13, 11, 5.5, 'a-metal'),
    line('a-line', 'M5.5 18v12'),
    ellipse('a-tip', -15, 1, 8.5, 10.5),
    ellipse('a-body', 0, 0, 18, 16),
    rect(0, -12, 10, 3, 1.5, 'a-detail'),
  ]),
  ellipse('a-shade', 48, 117, 11, 2.5),
];
const classicBud = (stem: number): Shape[] => [
  group(`translate(46 ${98 - stem}) rotate(-8)`, [
    rect(2, 0, 11, stem, 5.5, 'a-body'),
    rect(2, stem - 9, 11, 9, 4.5, 'a-metal'),
    ellipse('a-body', -2, -4, 15, 14),
    ellipse('a-detail', -12, -4, 2.2, 5.5, 0.7),
    rect(-1, -15, 9, 2.5, 1.25, 'a-detail'),
  ]),
  ellipse('a-shade', 60, 120, 10, 2.5),
];
const pairOf = (bud: Shape[]): Shape[] => [...bud, group('translate(240 0) scale(-1 1)', bud)];
const dashed = { 'stroke-dasharray': '5 4', 'stroke-width': '1.5' };
type ArtName = ArtModel | 'empty';
const ARTS: Record<ArtName, () => Shape[]> = {
  pro: () => [
    ellipse('a-shade', 120, 119, 48, 3.5),
    rect(78, 50, 84, 66, 22, 'a-body'),
    line('a-line', 'M78.5 72h83'),
    rect(117.5, 96, 5, 2.5, 1.25, 'a-detail'),
    ...pairOf(proBud),
  ],
  airpods: () => [
    ellipse('a-shade', 120, 121, 36, 3.5),
    rect(90, 32, 60, 86, 20, 'a-body'),
    line('a-line', 'M90.5 56h59'),
    rect(118.5, 80, 3, 3, 1.5, 'a-detail'),
    ...pairOf(classicBud(60)),
  ],
  airpods4: () => [
    ellipse('a-shade', 120, 120, 40, 3.5),
    rect(84, 46, 72, 70, 24, 'a-body'),
    line('a-line', 'M84.5 66h71'),
    rect(118.5, 98, 3, 3, 1.5, 'a-detail'),
    ...pairOf(classicBud(44)),
  ],
  max: () => [
    ellipse('a-shade', 120, 126, 78, 3.5),
    line('a-arm', 'M66 86V66M174 86V66'),
    line('a-bandline', 'M66 68C66 14 174 14 174 68'),
    line('a-band', 'M66 68C66 14 174 14 174 68'),
    rect(44, 78, 46, 46, 16, 'a-body'),
    rect(82, 82, 12, 38, 6, 'a-shade'),
    rect(150, 78, 46, 46, 16, 'a-body'),
    rect(146, 82, 12, 38, 6, 'a-shade'),
    rect(180, 71, 9, 9, 3, 'a-metal'),
    ['rect', { class: 'a-detail', x: 58, y: 84, width: 3, height: 10, rx: 1.5, opacity: 0.5 }],
  ],
  empty: () => [
    ['rect', { class: 'a-line', x: 82, y: 18, width: 76, height: 100, rx: 24, ...dashed }],
    ['path', { class: 'a-line', d: 'M83 46h74', ...dashed }],
    ['circle', { class: 'a-line', cx: 120, cy: 88, r: 3, 'stroke-width': '1.5' }],
  ],
};

const draw = (parent: Element, shapes: Shape[]): void => {
  for (const shape of shapes) {
    const [tag, attrs, children] = typeof shape === 'string' ? ['path' as const, { d: shape }, undefined] : shape;
    const el = document.createElementNS(SVG, tag);
    for (const [name, value] of Object.entries(attrs)) {
      el.setAttribute(name, String(value));
    }
    if (children) {
      draw(el, children);
    }
    parent.append(el);
  }
};

const svg = (cls: string, viewBox: string, shapes: Shape[]): SVGSVGElement => {
  const el = document.createElementNS(SVG, 'svg');
  el.setAttribute('class', cls);
  el.setAttribute('viewBox', viewBox);
  el.setAttribute('aria-hidden', 'true');
  draw(el, shapes);
  return el;
};

export const Icon = (p: { name: IconName; class?: string }): SVGSVGElement =>
  svg(`wp-icon${p.name === 'bolt' || p.name === 'heart' ? ' wp-icon--fill' : ''}${p.class ? ` ${p.class}` : ''}`, '0 0 16 16', ICONS[p.name]);

export const Glyph = (p: { name: GlyphName; class?: string }): SVGSVGElement =>
  svg(p.class ? `wp-icon ${p.class}` : 'wp-icon', '0 0 24 24', GLYPHS[p.name]);

/* role="img"; the caller sets its aria-label (localized). */
export const Art = (p: { model: ArtName; class: string }): SVGSVGElement => {
  const el = svg(`wp-art ${p.class}`, '0 0 240 132', ARTS[p.model]());
  el.removeAttribute('aria-hidden');
  el.setAttribute('role', 'img');
  return el;
};

export const Spinner = (p: { class?: string }) => (
  <svg class={p.class ? `wp-spinner ${p.class}` : 'wp-spinner'} viewBox="0 0 16 16" aria-hidden="true">
    <circle class="wp-spinner__track" cx="8" cy="8" r="6" />
    <circle cx="8" cy="8" r="6" stroke-dasharray="12 40" />
  </svg>
);
