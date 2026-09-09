// CPT background service worker.
//
// Holds its own WebSocket to CPT.Shell using adapter id "browser-selection"
// so it can forward right-click context-menu translations from ANY page,
// independent of the Gemini content script's connection.
//
// MV3 service workers can be torn down. We open the WS lazily on demand and
// reconnect on use. While the WS is open, Chrome keeps the worker alive.

const BRIDGE = 'ws://127.0.0.1:17872/host?adapter=browser-selection';
let ws = null;

function ensureSocket() {
  return new Promise((resolve) => {
    if (ws && ws.readyState === WebSocket.OPEN) return resolve(true);
    if (ws && ws.readyState === WebSocket.CONNECTING) {
      ws.addEventListener('open',  () => resolve(true),  { once: true });
      ws.addEventListener('error', () => resolve(false), { once: true });
      return;
    }
    try {
      ws = new WebSocket(BRIDGE);
      ws.addEventListener('open',  () => resolve(true),  { once: true });
      ws.addEventListener('error', () => { ws = null; resolve(false); }, { once: true });
      ws.addEventListener('close', () => { ws = null; });
    } catch { resolve(false); }
  });
}

async function sendFinal(text) {
  if (!text || !text.trim()) return false;
  const ok = await ensureSocket();
  if (!ok) return false;
  ws.send(JSON.stringify({ type: 'final', adapter: 'browser-selection', text: text.trim() }));
  return true;
}

// --- Context menu -------------------------------------------------------
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: 'cpt-translate-selection',
    title: 'Translate with CPT',
    contexts: ['selection']
  });
  chrome.contextMenus.create({
    id: 'cpt-translate-page',
    title: 'Translate page text with CPT',
    contexts: ['page']
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'cpt-translate-selection' && info.selectionText) {
    const ok = await sendFinal(info.selectionText);
    notify(tab, ok ? 'Sent to CPT.' : 'CPT not running.');
    return;
  }
  if (info.menuItemId === 'cpt-translate-page' && tab && tab.id != null) {
    try {
      const [{ result }] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        func: () => {
          // Prefer the current selection, fall back to the article/main body.
          const sel = window.getSelection && String(window.getSelection());
          if (sel && sel.trim().length > 0) return sel;
          const article = document.querySelector('article, main, [role="main"]');
          return (article ? article.innerText : document.body.innerText) || '';
        }
      });
      const ok = await sendFinal(result || '');
      notify(tab, ok ? 'Sent to CPT.' : 'CPT not running.');
    } catch (e) {
      notify(tab, 'Capture failed: ' + e.message);
    }
  }
});

function notify(tab, msg) {
  if (!tab || tab.id == null) return;
  chrome.scripting.executeScript({
    target: { tabId: tab.id },
    func: (m) => {
      const el = document.createElement('div');
      el.textContent = m;
      Object.assign(el.style, {
        position: 'fixed', right: '16px', bottom: '16px', zIndex: 2147483647,
        background: '#0a1420', color: '#7dd3fc', padding: '8px 12px',
        border: '1px solid #22d3ee', borderRadius: '6px', font: '12px Consolas',
        opacity: '0', transition: 'opacity 200ms'
      });
      document.body.appendChild(el);
      requestAnimationFrame(() => el.style.opacity = '1');
      setTimeout(() => { el.style.opacity = '0'; setTimeout(() => el.remove(), 300); }, 1800);
    },
    args: [msg]
  }).catch(() => {});
}

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
