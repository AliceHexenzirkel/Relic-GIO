// RPC bridge to the C# backend over WebView2 postMessage.
// rpc(type, payload) -> Promise(result); on(event, handler) for pushed events.
(function () {
  const pending = new Map();
  const listeners = new Map();
  let seq = 0;

  const wv = window.chrome && window.chrome.webview;

  if (wv) {
    wv.addEventListener('message', (e) => {
      const m = e.data;
      if (!m) return;
      if (m.id && pending.has(m.id)) {
        const { resolve, reject } = pending.get(m.id);
        pending.delete(m.id);
        m.ok ? resolve(m.result) : reject(new Error(m.error || 'Unknown error'));
      } else if (m.event) {
        (listeners.get(m.event) || []).forEach((h) => {
          try { h(m.data); } catch (err) { console.error(err); }
        });
      }
    });
  }

  window.rpc = function (type, payload) {
    if (!wv) return Promise.reject(new Error('WebView2 bridge unavailable.'));
    const id = 'r' + (++seq);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      wv.postMessage(JSON.stringify({ id, type, payload: payload || {} }));
    });
  };

  window.on = function (event, handler) {
    if (!listeners.has(event)) listeners.set(event, []);
    listeners.get(event).push(handler);
  };
})();
