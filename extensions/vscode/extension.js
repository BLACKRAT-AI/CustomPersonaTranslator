// CPT VS Code bridge.
//
// Strategy for Claude Code:
//   Claude Code's webview events are not yet a documented public API. Until
//   they are, we use three signals:
//     1. Active text editor change + selection — manual capture command for
//        explicit "translate this" actions.
//     2. Terminal data events — when a Claude Code session runs in an
//        integrated terminal, we sniff for the assistant prompt sentinel and
//        a quiet period to emit `final` events.
//     3. File watcher on `~/.claude/projects/*/transcripts.jsonl` (Claude
//        Code's local transcript log, if present) to read assistant turns
//        in real time without any UI hook.
//
//   The active strategy is configurable via the `cpt.claudeStrategy` setting
//   ("terminal" | "transcripts" | "selection"). Default: terminal.

const vscode = require('vscode');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const WebSocket = require('ws');

const BRIDGE = 'ws://127.0.0.1:17872/host?adapter=vscode';
const STABILITY_MS = 1200;

let ws = null;
let retryMs = 1000;

function connect() {
  ws = new WebSocket(BRIDGE);
  ws.on('open', () => { retryMs = 1000; });
  ws.on('close', () => { ws = null; setTimeout(connect, retryMs); retryMs = Math.min(retryMs*2, 15000); });
  ws.on('error', () => { try { ws.close(); } catch {} });
  ws.on('message', onShellMessage);
}
function send(obj) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj)); }

function onShellMessage(raw) {
  let m; try { m = JSON.parse(raw.toString()); } catch { return; }
  if (m.type === 'inject') {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    editor.edit(b => b.insert(editor.selection.active, m.text || ''));
  }
}

// --- Strategy: terminal sniff -------------------------------------------
function watchTerminals() {
  let buf = '';
  let timer = null;
  const flush = () => {
    if (buf.trim().length === 0) return;
    send({ type: 'final', adapter: 'vscode', text: buf.trim() });
    buf = '';
  };
  if (vscode.window.onDidWriteTerminalData) {
    vscode.window.onDidWriteTerminalData(e => {
      buf += e.data;
      if (timer) clearTimeout(timer);
      timer = setTimeout(flush, STABILITY_MS);
    });
  }
}

// --- Strategy: transcript file watch ------------------------------------
function watchClaudeTranscripts() {
  const root = path.join(os.homedir(), '.claude', 'projects');
  if (!fs.existsSync(root)) return;
  const watcher = vscode.workspace.createFileSystemWatcher(
    new vscode.RelativePattern(root, '**/*.jsonl')
  );
  const offsets = new Map();
  const handle = (uri) => {
    fs.stat(uri.fsPath, (err, st) => {
      if (err) return;
      const prev = offsets.get(uri.fsPath) || 0;
      if (st.size <= prev) { offsets.set(uri.fsPath, st.size); return; }
      const fd = fs.createReadStream(uri.fsPath, { start: prev, end: st.size });
      let chunks = '';
      fd.on('data', d => chunks += d.toString('utf8'));
      fd.on('end', () => {
        offsets.set(uri.fsPath, st.size);
        for (const line of chunks.split('\n')) {
          if (!line.trim()) continue;
          let rec; try { rec = JSON.parse(line); } catch { continue; }
          // Heuristic: emit when role is assistant and message.stop_reason is present.
          if (rec.type === 'assistant' && rec.message && (rec.message.stop_reason || rec.message.role === 'assistant')) {
            const text = extractText(rec.message);
            if (text) send({ type: 'final', adapter: 'vscode', text });
          }
        }
      });
    });
  };
  watcher.onDidChange(handle);
  watcher.onDidCreate(handle);
}

function extractText(message) {
  if (!message || !message.content) return '';
  if (typeof message.content === 'string') return message.content;
  if (Array.isArray(message.content)) {
    return message.content
      .filter(c => c.type === 'text' && typeof c.text === 'string')
      .map(c => c.text).join('\n');
  }
  return '';
}

function activate(context) {
  connect();

  const cfg = () => vscode.workspace.getConfiguration('cpt');
  const strategy = cfg().get('claudeStrategy', 'terminal');
  if (strategy === 'terminal' || strategy === 'both') watchTerminals();
  if (strategy === 'transcripts' || strategy === 'both') watchClaudeTranscripts();

  context.subscriptions.push(
    vscode.commands.registerCommand('cpt.toggle', () => {
      if (ws) { try { ws.close(); } catch {} } else connect();
    }),
    vscode.commands.registerCommand('cpt.captureSelection', () => {
      const ed = vscode.window.activeTextEditor;
      if (!ed) return;
      const text = ed.document.getText(ed.selection);
      if (text.trim()) send({ type: 'final', adapter: 'vscode', text });
    })
  );
}

function deactivate() { try { ws && ws.close(); } catch {} }

module.exports = { activate, deactivate };
