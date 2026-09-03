# CPT Discord Sidecar

Node.js bot that brings the **voice** and **text** Discord IOProviders to the
CustomPersonaTranslator desktop app. One bot handles both.

## Setup
1. Create a Discord application + bot at <https://discord.com/developers/applications>.
   Enable **Message Content** and **Voice State** privileged intents.
2. Invite the bot to your server with `bot` + `applications.commands` scopes
   and at minimum: View Channels, Send Messages, Connect, Speak.
3. Copy `.env.example` to `.env` and fill in token, guild id, voice channel id,
   text channel id.
4. `npm install`
5. `npm start`

## Behavior
- **Text channel**: messages from non-bot users are forwarded to the desktop
  app over `ws://127.0.0.1:17872` as `{type:'final', source:'text', text}`.
  When the persona has something to say, the desktop app sends back
  `{type:'inject', target:'text', text}` and the bot posts it as the persona.
- **Voice channel**: bot joins the configured voice channel, subscribes to
  per-user audio streams, and saves each completed utterance (silence-trimmed)
  as a WAV file. The desktop app does STT and persona rewrite, then sends back
  `{type:'inject', target:'voice', audioFile:'/path/to/pcm.wav'}` to play
  the persona voice in the channel.

## Consent
On startup the bot posts a notice in the text channel disclosing that messages
and voice are processed by CPT. Customize that string in `src/index.js`.

## Notes
- Voice receive depends on `@discordjs/voice` and `prism-media` decoding Opus
  via `@discordjs/opus`. On Windows you may need build tools (`npm install
  --global windows-build-tools`) for native modules; if that fails, use
  `opusscript` as a pure-JS fallback (slower).
- All audio stays on `127.0.0.1` between the bot and the desktop app — nothing
  about CPT itself touches a cloud service.
