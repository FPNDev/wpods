/* WPods bundle (app subset): icons, device glyphs, model illustrations, spinner, hydrate.
   Trimmed from the design system's components/bundle.js: tray/app-icon geometry, battery-ring markup
   and the React faces are design-canvas tools the app does not use. Assigns window.WPods. */
(function () {
  'use strict';

  /* 16x16 stroke icons, 1.5 stroke, round caps. FILL marks the two solid glyphs. */
  var ICONS = {
    'chevron-down': '<path d="M4 6l4 4 4-4"/>',
    'chevron-right': '<path d="M6 4l4 4-4 4"/>',
    'chevron-left': '<path d="M10 4L6 8l4 4"/>',
    'close': '<path d="M4 4l8 8M12 4l-8 8"/>',
    'minimize': '<path d="M3.5 8h9"/>',
    'check': '<path d="M3.5 8.5l3 3 6-7"/>',
    'plus': '<path d="M8 3v10M3 8h10"/>',
    'bolt': '<path d="M9.2 1L3.4 9h4.1l-.9 6 5.9-8.1H8.4z"/>',
    'heart': '<path d="M8 14S2.25 10.6 2.25 6.6A3.1 3.1 0 0 1 8 4.9a3.1 3.1 0 0 1 5.75 1.7C13.75 10.6 8 14 8 14z"/>',
    'info': '<circle cx="8" cy="8" r="6.25"/><path d="M8 7.25v4M8 4.9v.1"/>',
    'warning': '<path d="M7.13 2.6a1 1 0 0 1 1.74 0l5.4 9.4a1 1 0 0 1-.87 1.5H2.6a1 1 0 0 1-.87-1.5z"/><path d="M8 6.25v3M8 11.25v.1"/>',
    'error': '<circle cx="8" cy="8" r="6.25"/><path d="M5.9 5.9l4.2 4.2M10.1 5.9l-4.2 4.2"/>',
    'check-circle': '<circle cx="8" cy="8" r="6.25"/><path d="M5.25 8.25l1.9 1.9 3.6-4.15"/>',
    'mic': '<rect x="5.75" y="1.75" width="4.5" height="8" rx="2.25"/><path d="M3.25 7.75a4.75 4.75 0 0 0 9.5 0M8 12.5v1.75"/>',
    'pc': '<rect x="1.75" y="2.75" width="12.5" height="8.5" rx="1.5"/><path d="M5.5 13.75h5M8 11.25v2.5"/>',
    'press': '<rect x="5.75" y="6.75" width="4.5" height="7.5" rx="2.25"/><path d="M8 1.75v2.5M4.25 3.25l1.25 1.25M11.75 3.25L10.5 4.5"/>',
    'external': '<path d="M9.5 2.25h4.25V6.5M13.75 2.25L8 8M12 9.75V12.5a1.25 1.25 0 0 1-1.25 1.25H3.5a1.25 1.25 0 0 1-1.25-1.25V5.25A1.25 1.25 0 0 1 3.5 4h2.75"/>',
    'bluetooth': '<path d="M4.5 5.25l7 5.5L8 13.75V2.25l3.5 3-7 5.5"/>',
    'power': '<path d="M8 1.75v5.5"/><path d="M4.6 4.1a5.25 5.25 0 1 0 6.8 0"/>',
    'double': '<circle cx="5.25" cy="8" r="2.25"/><circle cx="10.75" cy="8" r="2.25"/>',
    'hold': '<circle cx="8" cy="8" r="2.25"/><path d="M8 1.75a6.25 6.25 0 1 1-6.25 6.25"/>',
    'search': '<circle cx="7" cy="7" r="4.75"/><path d="M10.5 10.5l3.75 3.75"/>',
    'globe': '<circle cx="8" cy="8" r="6.25"/><path d="M1.75 8h12.5M8 1.75c1.9 1.7 2.85 3.8 2.85 6.25S9.9 12.55 8 14.25C6.1 12.55 5.15 10.45 5.15 8S6.1 3.45 8 1.75z"/>',
    'tone': '<path d="M5.25 12V3.5l7-1.5V10.5"/><circle cx="3.75" cy="12" r="1.75"/><circle cx="10.75" cy="10.5" r="1.75"/>',
    /* Noise-control modes */
    'nc-off': '<circle cx="8" cy="8" r="5.75"/><path d="M4 12L12 4"/>',
    'nc-anc': '<circle cx="8" cy="8" r="5.75"/><circle cx="8" cy="8" r="2.75" fill="currentColor"/>',
    'nc-transparency': '<circle cx="8" cy="8" r="5.75" stroke-dasharray="1.6 2"/><circle cx="8" cy="8" r="1.5" fill="currentColor"/>',
    'nc-adaptive': '<circle cx="8" cy="8" r="5.75"/><path d="M8 2.25a5.75 5.75 0 0 1 0 11.5z" fill="currentColor"/>'
  };
  var FILL = { bolt: 1, heart: 1 };

  /* 24x24 stroke device glyphs: rings, menus, row leads. */
  var BUD_L = 'M12 12.03A4.5 4.5 0 1 1 14.5 8V19a1.25 1.25 0 0 1-2.5 0Z';
  var BUD_R = 'M12 12.03A4.5 4.5 0 1 0 9.5 8V19a1.25 1.25 0 0 0 2.5 0Z';
  var GLYPHS = {
    'bud-left': '<path d="' + BUD_L + '"/>',
    'bud-right': '<path d="' + BUD_R + '"/>',
    'case-pro': '<rect x="3.5" y="6" width="17" height="13" rx="4"/><path d="M3.5 10.5h17M11.25 15h1.5"/>',
    'case': '<rect x="6" y="3.5" width="12" height="17" rx="4"/><path d="M6 8.5h12M11.5 14.5h1"/>',
    'max': '<path d="M5.5 13v-2a6.5 6.5 0 0 1 13 0v2"/><rect x="3.75" y="12.25" width="4.5" height="7.5" rx="2"/><rect x="15.75" y="12.25" width="4.5" height="7.5" rx="2"/>',
    'pair': '<g transform="translate(-4.5 0)"><path d="' + BUD_L + '"/></g><g transform="translate(4.5 0)"><path d="' + BUD_R + '"/></g>'
  };

  /* Model illustrations, 240x132. Classes (bundle.css .wp-art) bind every fill to an art-* token. */
  function proBud() {
    return '<g transform="translate(48 60) rotate(6)">' +
      '<rect class="a-body" x="-1" y="4" width="13" height="46" rx="6.5"/>' +
      '<rect class="a-metal" x="-1" y="39" width="13" height="11" rx="5.5"/>' +
      '<path class="a-line" d="M5.5 18v12"/>' +
      '<ellipse class="a-tip" cx="-15" cy="1" rx="8.5" ry="10.5"/>' +
      '<ellipse class="a-body" cx="0" cy="0" rx="18" ry="16"/>' +
      '<rect class="a-detail" x="0" y="-12" width="10" height="3" rx="1.5"/>' +
      '</g><ellipse class="a-shade" cx="48" cy="117" rx="11" ry="2.5"/>';
  }
  function classicBud(stem) {
    return '<g transform="translate(46 ' + (98 - stem) + ') rotate(-8)">' +
      '<rect class="a-body" x="2" y="0" width="11" height="' + stem + '" rx="5.5"/>' +
      '<rect class="a-metal" x="2" y="' + (stem - 9) + '" width="11" height="9" rx="4.5"/>' +
      '<ellipse class="a-body" cx="-2" cy="-4" rx="15" ry="14"/>' +
      '<ellipse class="a-detail" cx="-12" cy="-4" rx="2.2" ry="5.5" opacity="0.7"/>' +
      '<rect class="a-detail" x="-1" y="-15" width="9" height="2.5" rx="1.25"/>' +
      '</g><ellipse class="a-shade" cx="60" cy="120" rx="10" ry="2.5"/>';
  }
  function mirror(s) { return '<g transform="translate(240 0) scale(-1 1)">' + s + '</g>'; }
  var ART = {
    pro: '<ellipse class="a-shade" cx="120" cy="119" rx="48" ry="3.5"/>' +
      '<rect class="a-body" x="78" y="50" width="84" height="66" rx="22"/>' +
      '<path class="a-line" d="M78.5 72h83"/>' +
      '<rect class="a-detail" x="117.5" y="96" width="5" height="2.5" rx="1.25"/>' +
      proBud() + mirror(proBud()),
    airpods: '<ellipse class="a-shade" cx="120" cy="121" rx="36" ry="3.5"/>' +
      '<rect class="a-body" x="90" y="32" width="60" height="86" rx="20"/>' +
      '<path class="a-line" d="M90.5 56h59"/>' +
      '<rect class="a-detail" x="118.5" y="80" width="3" height="3" rx="1.5"/>' +
      classicBud(60) + mirror(classicBud(60)),
    airpods4: '<ellipse class="a-shade" cx="120" cy="120" rx="40" ry="3.5"/>' +
      '<rect class="a-body" x="84" y="46" width="72" height="70" rx="24"/>' +
      '<path class="a-line" d="M84.5 66h71"/>' +
      '<rect class="a-detail" x="118.5" y="98" width="3" height="3" rx="1.5"/>' +
      classicBud(44) + mirror(classicBud(44)),
    max: '<ellipse class="a-shade" cx="120" cy="126" rx="78" ry="3.5"/>' +
      '<path class="a-arm" d="M66 86V66M174 86V66"/>' +
      '<path class="a-bandline" d="M66 68C66 14 174 14 174 68"/>' +
      '<path class="a-band" d="M66 68C66 14 174 14 174 68"/>' +
      '<rect class="a-body" x="44" y="78" width="46" height="46" rx="16"/>' +
      '<rect class="a-shade" x="82" y="82" width="12" height="38" rx="6"/>' +
      '<rect class="a-body" x="150" y="78" width="46" height="46" rx="16"/>' +
      '<rect class="a-shade" x="146" y="82" width="12" height="38" rx="6"/>' +
      '<rect class="a-metal" x="180" y="71" width="9" height="9" rx="3"/>' +
      '<rect class="a-detail" x="58" y="84" width="3" height="10" rx="1.5" opacity="0.5"/>',
    empty: '<rect class="a-line" x="82" y="18" width="76" height="100" rx="24" stroke-dasharray="5 4" stroke-width="1.5"/>' +
      '<path class="a-line" d="M83 46h74" stroke-dasharray="5 4" stroke-width="1.5"/>' +
      '<circle class="a-line" cx="120" cy="88" r="3" stroke-width="1.5"/>'
  };
  var ART_LABEL = { pro: 'AirPods Pro and case', airpods: 'AirPods and case', airpods4: 'AirPods and case', max: 'AirPods Max', empty: 'No AirPods' };

  function icon(name, cls) {
    return '<svg class="wp-icon' + (FILL[name] ? ' wp-icon--fill' : '') + (cls ? ' ' + cls : '') + '" viewBox="0 0 16 16" aria-hidden="true">' + (ICONS[name] || '') + '</svg>';
  }
  function glyph(name, cls) {
    return '<svg class="wp-icon' + (cls ? ' ' + cls : '') + '" viewBox="0 0 24 24" aria-hidden="true">' + (GLYPHS[name] || '') + '</svg>';
  }
  function art(model, cls) {
    return '<svg class="wp-art' + (cls ? ' ' + cls : '') + '" viewBox="0 0 240 132" role="img" aria-label="' + (ART_LABEL[model] || 'AirPods') + '">' + (ART[model] || '') + '</svg>';
  }
  function spinner(cls) {
    return '<svg class="wp-spinner' + (cls ? ' ' + cls : '') + '" viewBox="0 0 16 16" aria-hidden="true"><circle class="wp-spinner__track" cx="8" cy="8" r="6"/><circle cx="8" cy="8" r="6" stroke-dasharray="12 40"/></svg>';
  }

  /* Swap <span data-icon="name"> / data-glyph / data-art / data-spinner placeholders for real markup. */
  function hydrate(root) {
    root = root || document;
    var n, i, list;
    list = root.querySelectorAll('[data-icon]');
    for (i = 0; i < list.length; i++) { n = list[i]; n.outerHTML = icon(n.getAttribute('data-icon'), n.getAttribute('data-class')); }
    list = root.querySelectorAll('[data-glyph]');
    for (i = 0; i < list.length; i++) { n = list[i]; n.outerHTML = glyph(n.getAttribute('data-glyph'), n.getAttribute('data-class')); }
    list = root.querySelectorAll('[data-art]');
    for (i = 0; i < list.length; i++) { n = list[i]; n.outerHTML = art(n.getAttribute('data-art'), n.getAttribute('data-class')); }
    list = root.querySelectorAll('[data-spinner]');
    for (i = 0; i < list.length; i++) { n = list[i]; n.outerHTML = spinner(n.getAttribute('data-class')); }
  }

  window.WPods = { icon: icon, glyph: glyph, art: art, spinner: spinner, hydrate: hydrate };
  if (document.readyState !== 'loading') hydrate(document);
  else document.addEventListener('DOMContentLoaded', function () { hydrate(document); });
})();
