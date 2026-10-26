/* WPods app icon generator, trimmed verbatim from the design bundle (components/bundle.js).
   Assigns window.WPods.appIcon(size): exact SVG for each rendition (16, 20, 24, 32 hinted; 40, 48, 64, 96, 256 from the master). */
(function () {
  "use strict";

  /* Shared bud silhouette (head + inner stem), used by the app icon. */
  function f(x) {
    return Math.round(x * 1000) / 1000;
  }
  function budPath(b, bottom) {
    var rs = b.sw / 2;
    if (b.side === "L") {
      var xi = b.sx,
        yi = b.cy + Math.sqrt(b.r * b.r - (xi - b.cx) * (xi - b.cx));
      return (
        "M" +
        f(xi) +
        " " +
        f(yi) +
        "A" +
        b.r +
        " " +
        b.r +
        " 0 1 1 " +
        f(b.cx + b.r) +
        " " +
        b.cy +
        "V" +
        f(bottom - rs) +
        "A" +
        rs +
        " " +
        rs +
        " 0 0 1 " +
        f(xi) +
        " " +
        f(bottom - rs) +
        "Z"
      );
    }
    var xr = b.sx + b.sw,
      yr = b.cy + Math.sqrt(b.r * b.r - (xr - b.cx) * (xr - b.cx));
    return (
      "M" +
      f(xr) +
      " " +
      f(yr) +
      "A" +
      b.r +
      " " +
      b.r +
      " 0 1 0 " +
      f(b.cx - b.r) +
      " " +
      b.cy +
      "V" +
      f(bottom - rs) +
      "A" +
      rs +
      " " +
      rs +
      " 0 0 0 " +
      f(xr) +
      " " +
      f(bottom - rs) +
      "Z"
    );
  }

  /* App icon */
  var APP_SMALL = {
    16: {
      rx: 3.5,
      buds: [
        { cx: 4.5, cy: 6, r: 2.5, sx: 5, sw: 2, side: "L" },
        { cx: 11.5, cy: 6, r: 2.5, sx: 9, sw: 2, side: "R" },
      ],
      bottom: 13,
    },
    20: {
      rx: 4.5,
      buds: [
        { cx: 6, cy: 7.5, r: 3, sx: 7, sw: 2, side: "L" },
        { cx: 14, cy: 7.5, r: 3, sx: 11, sw: 2, side: "R" },
      ],
      bottom: 16,
    },
    24: {
      rx: 5.5,
      buds: [
        { cx: 7, cy: 9, r: 4, sx: 8, sw: 3, side: "L" },
        { cx: 17, cy: 9, r: 4, sx: 13, sw: 3, side: "R" },
      ],
      bottom: 20,
    },
    32: {
      rx: 7,
      buds: [
        { cx: 10, cy: 12, r: 5, sx: 11, sw: 4, side: "L" },
        { cx: 22, cy: 12, r: 5, sx: 17, sw: 4, side: "R" },
      ],
      bottom: 26,
    },
  };
  var APP = {
    top: "#5aaeff",
    bottom: "#0b5cd5",
    bud: "#ffffff",
    budShade: "#dde4ee",
    tip: "#c3cede",
    vent: "#1b3a66",
    wave: "#ffffff",
    shadow: "#00265e",
  };
  function appIcon(size) {
    var id = "a" + size;
    if (APP_SMALL[size]) {
      var s = APP_SMALL[size],
        buds = "";
      s.buds.forEach(function (b) {
        buds +=
          '<path d="' + budPath(b, s.bottom) + '" fill="' + APP.bud + '"/>';
      });
      return (
        '<svg xmlns="http://www.w3.org/2000/svg" width="' +
        size +
        '" height="' +
        size +
        '" viewBox="0 0 ' +
        size +
        " " +
        size +
        '">' +
        '<defs><linearGradient id="' +
        id +
        'g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' +
        APP.top +
        '"/><stop offset="1" stop-color="' +
        APP.bottom +
        '"/></linearGradient></defs>' +
        '<rect width="' +
        size +
        '" height="' +
        size +
        '" rx="' +
        s.rx +
        '" fill="url(#' +
        id +
        'g)"/>' +
        buds +
        "</svg>"
      );
    }
    var detail = size >= 40,
      waves = size >= 64;
    function bud(mirrorIt) {
      var g =
        '<path d="' +
        budPath({ cx: 94, cy: 104, r: 30, sx: 108, sw: 16, side: "L" }, 196) +
        '" fill="url(#' +
        id +
        'b)"/>';
      if (detail)
        g =
          '<ellipse cx="66" cy="98" rx="12" ry="16" fill="' +
          APP.tip +
          '"/>' +
          g +
          '<rect x="96" y="83" width="16" height="4.5" rx="2.25" fill="' +
          APP.vent +
          '" opacity="0.75"/>' +
          '<rect x="108" y="180" width="16" height="16" rx="8" fill="' +
          APP.budShade +
          '"/>';
      return mirrorIt
        ? '<g transform="translate(256 0) scale(-1 1)">' + g + "</g>"
        : g;
    }
    return (
      '<svg xmlns="http://www.w3.org/2000/svg" width="' +
      size +
      '" height="' +
      size +
      '" viewBox="0 0 256 256">' +
      '<defs><linearGradient id="' +
      id +
      'g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' +
      APP.top +
      '"/><stop offset="1" stop-color="' +
      APP.bottom +
      '"/></linearGradient>' +
      '<linearGradient id="' +
      id +
      'b" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' +
      APP.bud +
      '"/><stop offset="1" stop-color="' +
      APP.budShade +
      '"/></linearGradient>' +
      '<filter id="' +
      id +
      's" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="6" stdDeviation="7" flood-color="' +
      APP.shadow +
      '" flood-opacity="0.35"/></filter></defs>' +
      '<rect x="16" y="16" width="224" height="224" rx="52" fill="url(#' +
      id +
      'g)"/>' +
      '<rect x="17" y="17" width="222" height="222" rx="51" fill="none" stroke="#ffffff" stroke-opacity="0.22" stroke-width="2"/>' +
      (waves
        ? '<path d="M101.5 61.5a52 52 0 0 1 53 0M90 44a72 72 0 0 1 76 0" fill="none" stroke="' +
          APP.wave +
          '" stroke-opacity="0.6" stroke-width="7" stroke-linecap="round"/>'
        : "") +
      '<g filter="url(#' +
      id +
      's)">' +
      bud(false) +
      bud(true) +
      "</g></svg>"
    );
  }

  window.WPods = { appIcon: appIcon };
})();
