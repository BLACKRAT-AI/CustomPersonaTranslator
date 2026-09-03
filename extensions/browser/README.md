# CPT Browser Bridge

MV3 browser extension with two roles:

1. **Gemini host adapter** — captures Gemini's final responses (stop-button +
   stability detection) and forwards them to the CustomPersonaTranslator
   desktop app over `ws://127.0.0.1:17872`.
2. **Right-click "Translate with CPT"** — works on **any page**. Select text
   → right-click → *Translate with CPT*. With nothing selected, right-click
   gives *Translate page text with CPT* (grabs the article / main body / page
   text). A small toast confirms the send.

Two separate WebSocket connections to CPT.Shell:
- `adapter=gemini` from the content script (page-driven done-detection)
- `adapter=browser-selection` from the background service worker (manual
  context-menu translations from any tab)

## Install (developer mode)
1. Open `chrome://extensions` or `edge://extensions`.
2. Toggle **Developer mode**.
3. **Load unpacked** → select this folder.

## Permissions
- `contextMenus` + `activeTab` — for the right-click menu.
- `scripting` — to inject the page-text grabber + toast on demand.
- `host_permissions` are narrow: Gemini, and loopback to CPT only.
