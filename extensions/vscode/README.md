# CPT — VS Code Bridge

Captures Claude Code (and other terminal-based) assistant final responses
and forwards them to the CustomPersonaTranslator desktop app at
`ws://localhost:17872`. Also lets the desktop app inject text into the
active editor.

## Strategies (`cpt.claudeStrategy`)
- **terminal** (default): sniffs the integrated terminal data stream and
  flushes a "final" event after `STABILITY_MS` of silence.
- **transcripts**: watches `~/.claude/projects/**/*.jsonl` for assistant
  turns and emits them as they're appended. Best fidelity when Claude Code
  is writing transcripts locally.
- **both**: enable both strategies.

## Install (dev)
```
cd extensions/vscode
npm install
code --extensionDevelopmentPath="$(pwd)"
```
