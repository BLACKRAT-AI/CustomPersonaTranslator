# CustomPersonaTranslator

A local-first Windows desktop app that puts a persona in front of a coding
agent. You talk to it; it drives your coding CLI; the answer comes back in a
chosen character's voice, spoken through a locally-cloned (or preset) voice and
visualised as a glitch-hologram summoned from the system tray.

Speech recognition and speech synthesis run on your machine. Requests and persona
rewrites use the coding CLI you selected and its signed-in service. Optional
Wikipedia/Wikiquote research also uses the network.

The compact floating panel provides persona selection, microphone and standby controls, agent access, and a hologram. Persona settings include custom head models, projection styles, and Original/Turbo voice cloning. Agents support task-specific spoken openings and desktop tools.

Run development commands below from this `dev` directory. The repository-root `CPT.exe` launches the packaged build in `dev/app`. Rebuild that package with `powershell -File scripts/build-root.ps1`.

This is a personal, experimental project. Use at your own risk; see the root README.

## Connecting a coding CLI

CPT drives one of four coding CLIs, and sets it up for you:

| CLI | Vendor | Installed as |
| --- | --- | --- |
| Claude Code | Anthropic | `@anthropic-ai/claude-code` |
| Gemini CLI | Google | `@google/gemini-cli` |
| Codex CLI | OpenAI | `@openai/codex` |
| GitHub Copilot CLI | GitHub | `@github/copilot` |

Open **Connect a coding CLI…** from the tray menu (or click the status strip on
the persona bar). Pick one and CPT installs it with npm, verifies it, and hosts
its sign-in inside its own window. **Signing in is the only step that is yours** —
everything else happens without being asked.

Conversation turns run through each CLI's non-interactive prompt mode with
redirected pipes, which is what makes the reply parseable rather than a terminal
redraw. A pseudo-console is used only for the sign-in, because that is the one
flow that genuinely needs a terminal.

If a CLI renames a flag, drop a corrected definition in
`%LOCALAPPDATA%\CustomPersonaTranslator\cli-providers\<id>.json` — it overrides
the built-in one without needing a new build.

## Standby mode

Turn on **Standby** (the waveform button on the bar, `Ctrl+Shift+S`, or the tray
menu) and CPT listens continuously:

1. Say the **wake phrase** — "hey agent" by default.
2. Ask your question. Keep talking; it accumulates.
3. Pause when you finish; the request goes to the agent after the configured silence interval.

"Never mind" throws the request away. Wake and cancellation phrases, the silence
interval, and microphone sensitivity are configurable in Settings → Standby.

Audio is segmented locally by a voice-activity detector, so speech recognition
only runs on complete utterances rather than continuously on a silent room.

## Layout

```
src/CPT.Shell           WPF app (tray, persona bar, hologram, editors, setup)
src/CPT.Core            C# class library
  Cli/                  Provider catalog, probe, installer, agent turns
  Cli/Streaming/        Per-CLI output decoding into neutral turn events
  Pty/                  Windows pseudo-console, used for interactive sign-in
  Voice/                Wake-phrase state machine, voice-activity detection
  Stt/                  Mic capture (push-to-talk and continuous) + whisper.cpp
  Tts/                  Piper subprocess, Chatterbox cloning, streaming player
  Filter/               Markdown to spoken-text content filter
  Llm/                  llama.cpp server + streaming client
  Pipeline/             End-to-end TranslationPipeline
  Personas/             PersonaStore, PersonaBuilder, packaging, YouTube import
  Research/             PersonaResearchAgent (Wikiquote, Wikipedia)
  Ipc/                  WebSocket server for browser and editor adapters
  Settings/             AppSettings (JSON)
src/CPT.HologramWeb     HTML + WebGL shader for the persona hologram
extensions/browser      MV3 browser extension
extensions/vscode       VS Code extension
sidecars/discord        Node.js Discord bot (voice + text IOProviders)
tests/CPT.Tests         Unit tests (xunit)
tests/CPT.Smoke         Manual integration harness
```

## Prerequisites

- Windows 11, .NET 10 SDK
- Node.js 20+ — needed for CPT to install a coding CLI for you
- [Piper](https://github.com/rhasspy/piper) plus a voice model (.onnx)
- [whisper.cpp](https://github.com/ggerganov/whisper.cpp) `whisper-cli` plus a
  ggml model (e.g. `ggml-base.en.bin`) — required for push-to-talk and standby
- WebView2 Runtime (pre-installed on Windows 11)
- A signed-in coding CLI for requests and persona rewrites

`scripts\bootstrap.ps1` fetches the speech tools. Paths can be overridden in
Settings, or directly in
`%LOCALAPPDATA%\CustomPersonaTranslator\settings.json`.

## Build and run

```
dotnet build
dotnet test
dotnet run --project src/CPT.Shell
```

`dotnet run --project src/CPT.Shell -- --setup` opens the CLI setup screen on
launch, which is what the installer does after a first install.

To check the agent path against a real CLI without the UI:

```
dotnet run --project tests/CPT.Smoke -- agent "Reply with exactly: pipeline ok"
```

To verify real file actions and conversation isolation with the configured CLI
(uses the signed-in service and creates a disposable folder under `%TEMP%`):

```
dotnet run --project tests/CPT.Smoke -- verify-agent
dotnet run --project tests/CPT.Smoke -- verify-voice
dotnet run --project tests/CPT.UiSmoke -- --verify-turn
```

The voice protocol check uses a small test server and the configured Python
runtime. The final check runs the real app service and plays a spoken answer.

## Controls

- Tray left-click, or **Ctrl+Shift+Space** — show or hide the persona.
- **Ctrl+Shift+M** — push to talk.
- **Ctrl+Shift+S** — toggle standby listening.
- On the bar: menu, hold-to-talk, standby, avatar, pin, hide, and a status strip
  showing which CLI is linked. Click the strip to open setup.

## Other hosts

CPT still listens to agents it does not own, so their replies are spoken in
persona too:

- **Browser** — load `extensions/browser` unpacked in Chrome or Edge.
- **VS Code** — load `extensions/vscode` as a development extension.
- **Native apps** — the universal UI Automation adapter; add profiles in
  `adapters/*.json`.
- **Discord** — `cd sidecars/discord && npm install && npm start`.

## Code standards

The solution builds warning-clean with .NET's recommended analyzers and
`TreatWarningsAsErrors`, configured once in `Directory.Build.props`. The pure
logic — phrase matching, the standby state machine, voice-activity detection,
CLI output decoding, argument building — is covered by unit tests in
`tests/CPT.Tests`; run them with `dotnet test`.
