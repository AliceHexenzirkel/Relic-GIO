// Minimal i18n layer for the Relic UI.
//
//   t(key, vars)   -> plain text (NEVER HTML). English is embedded (lang/en.js -> window.RELIC_EN)
//                     so it needs no fetch; other languages are fetched from lang/<code>.json and
//                     overlay English; a missing key falls back to English, then to the key itself.
//   I18N.init(code) -> Promise; resolves when the overlay (if any) is loaded.
//   I18N.available() -> [{code, name}] from lang/index.json (English always present).
//
// Keys are flat, dot-namespaced ("login.enter", "core.play.stoppingFiddler"). The same JSON files
// are read by the C# side (Relic.Core/Util/L.cs), so a key added for the UI is also usable in C#.
// Placeholders are {name}; HTML insertion goes through T()/tf() in app.js, never raw t().
(function () {
  var EN = window.RELIC_EN || {};
  var cur = EN;
  var code = 'en';
  var index = [{ code: 'en', name: 'English' }];

  function fmt(s, vars) {
    if (!vars) return s;
    return s.replace(/\{(\w+)\}/g, function (m, k) {
      return Object.prototype.hasOwnProperty.call(vars, k) ? String(vars[k]) : m;
    });
  }

  function t(key, vars) {
    var s = cur[key];
    if (s === undefined) s = EN[key];
    if (s === undefined) s = key;
    return fmt(s, vars);
  }

  function has(key) { return cur[key] !== undefined || EN[key] !== undefined; }

  async function fetchJson(url) {
    var r = await fetch(url, { cache: 'no-cache' });
    if (!r.ok) throw new Error(url + ' -> ' + r.status);
    return await r.json();
  }

  async function loadIndex() {
    try {
      var list = await fetchJson('lang/index.json');
      if (Array.isArray(list) && list.length) {
        // English is always offered; a .bak file is never a language.
        var seen = {};
        index = [];
        list.forEach(function (e) {
          if (!e || typeof e.code !== 'string' || !/^[a-z]{2,3}(-[A-Za-z0-9]+)?$/.test(e.code)) return;
          if (seen[e.code]) return;
          seen[e.code] = true;
          index.push({ code: e.code, name: String(e.name || e.code) });
        });
        if (!seen.en) index.unshift({ code: 'en', name: 'English' });
      }
    } catch (e) { /* keep the English-only default */ }
    return index;
  }

  async function init(lang) {
    await loadIndex();
    lang = (lang || 'en').toLowerCase();
    if (lang === 'en') { cur = EN; code = 'en'; }
    else {
      try {
        var ov = await fetchJson('lang/' + encodeURIComponent(lang) + '.json');
        cur = Object.assign({}, EN, ov);
        code = lang;
      } catch (e) {
        cur = EN; code = 'en';
      }
    }
    try { document.documentElement.lang = code; } catch (e) { /* ignore */ }
    return code;
  }

  window.I18N = {
    init: init,
    t: t,
    has: has,
    available: function () { return index.slice(); },
    get code() { return code; },
  };
  window.t = t;
})();
