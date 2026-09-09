// CPT Gemini bridge — content script.
//
// Strategy for "response complete" detection (Tier B + C):
//   Tier B: watch the send/stop button. While Gemini is generating, the button
//           is a "Stop" affordance (aria-label includes "Stop"). When it
//           reverts to "Send" / "Submit", the turn is done.
//   Tier C: stability fallback — if the last assistant message's text hash
//           doesn't change for STABILITY_MS, treat as done.
//
// Selectors are kept in one place so they can be revised as Gemini's DOM shifts.

const PROFILE = {
  // The container that holds the assistant's response stream.
  responseSelector: 'message-content, .model-response-text, [data-test-id="response-content"]',
  // Each individual assistant turn.
  turnSelector: 'model-response, .conversation-turn[data-role="model"], .response-container',
  // The user input textarea / contenteditable.
  inputSelector: 'rich-textarea div[contenteditable="true"], textarea[aria-label*="prompt" i]',
  // The send button (also used as stop-while-generating).
  sendButtonSelector: 'button[aria-label*="Send" i], button[aria-label*="Submit" i], button[mat-icon-button][aria-label]',
  stopButtonAriaSubstrings: ['stop'],
};

const STABILITY_MS = 1200;
const HOST = 'localhost:17872';

let ws = null;
let wsRetryMs = 1000;

function connect() {
  try {
    ws = new WebSocket(`ws://${HOST}/host?adapter=gemini`);
    ws.onopen = () => { wsRetryMs = 1000; sendStatus('connected'); };
    ws.onmessage = onShellMessage;
    ws.onclose = () => { ws = null; setTimeout(connect, wsRetryMs); wsRetryMs = Math.min(wsRetryMs * 2, 15000); };
    ws.onerror = () => { try { ws && ws.close(); } catch {} };
  } catch (_) {
    setTimeout(connect, wsRetryMs);
  }
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function sendStatus(state) { send({ type: 'status', adapter: 'gemini', state }); }

// Inject text into Gemini's input box and optionally submit.
function injectText(text, submit) {
  const input = document.querySelector(PROFILE.inputSelector);
  if (!input) { sendStatus('inject_failed_no_input'); return; }
  input.focus();
  if (input.tagName === 'TEXTAREA') {
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
    setter.call(input, text);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  } else {
    // contenteditable
    input.textContent = text;
    input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }));
  }
  if (submit) {
    // Best-effort submit: Enter key.
    setTimeout(() => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true }));
    }, 50);
  }
}

function onShellMessage(ev) {
  let msg; try { msg = JSON.parse(ev.data); } catch { return; }
  if (msg.type === 'inject') injectText(msg.text || '', !!msg.submit);
}

// --- Done detection ---------------------------------------------------------

function isGenerating() {
  const buttons = document.querySelectorAll(PROFILE.sendButtonSelector);
  for (const b of buttons) {
    const label = (b.getAttribute('aria-label') || '').toLowerCase();
    if (PROFILE.stopButtonAriaSubstrings.some(s => label.includes(s))) return true;
  }
  return false;
}

function getLatestTurn() {
  const turns = document.querySelectorAll(PROFILE.turnSelector);
  return turns.length ? turns[turns.length - 1] : null;
}

function getTurnText(node) {
  if (!node) return '';
  return (node.innerText || '').trim();
}

function tryExtractMarkdown(node) {
  // Markdown reconstruction: walk children, preserving code blocks, lists, paragraphs,
  // and skipping thinking/scratchpad blocks. Best-effort; the shell's content filter is
  // permissive about input shape.
  if (!node) return '';
  // Strip known "thinking" blocks before serializing.
  const clone = node.cloneNode(true);
  const thinking = clone.querySelectorAll(
    '[aria-label*="thinking" i], [data-thinking="true"], details[open] summary'
  );
  thinking.forEach(n => {
    // If it's a <summary>, also drop the parent <details>.
    const det = n.closest('details');
    if (det) det.remove(); else n.remove();
  });
  return (clone.innerText || '').trim();
}

const lastTurnState = new WeakMap(); // turnNode -> { hash, hashSetAt, announced }

function hash(s) {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return h;
}

function tick() {
  const turn = getLatestTurn();
  if (!turn) return;
  const text = getTurnText(turn);
  if (!text) return;

  const state = lastTurnState.get(turn) || { hash: 0, hashSetAt: 0, announced: false };
  const h = hash(text);
  const now = performance.now();

  if (h !== state.hash) {
    state.hash = h;
    state.hashSetAt = now;
    state.announced = false;
    lastTurnState.set(turn, state);
    // Always stream partials so the shell can begin rewrite/TTS on early sentences.
    send({ type: 'partial', adapter: 'gemini', text });
    return;
  }

  const stable = (now - state.hashSetAt) >= STABILITY_MS;
  const generating = isGenerating();

  if (!state.announced && stable && !generating) {
    state.announced = true;
    lastTurnState.set(turn, state);
    const markdown = tryExtractMarkdown(turn);
    send({ type: 'final', adapter: 'gemini', text: markdown || text });
  }
}

const observer = new MutationObserver(() => { /* tick on next animation frame */ });
observer.observe(document.body, { childList: true, subtree: true, characterData: true });

setInterval(tick, 200);
connect();
