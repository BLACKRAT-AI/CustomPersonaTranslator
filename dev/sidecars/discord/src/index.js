// CPT Discord sidecar.
//
// Architecture:
//   - One bot account hosts both voice and text providers.
//   - Bridge: a single WebSocket to the desktop app (CPT.Shell IpcServer)
//     using the "discord" adapter id.
//   - Inbound from shell:
//       { type: 'inject', text, submit, target: 'text'|'voice' }
//         For target=text: post the message to the configured text channel.
//         For target=voice: stream the supplied 16-bit PCM (base64) into the
//         current voice connection.
//   - Outbound to shell:
//       { type: 'final', adapter: 'discord', source: 'text'|'voice',
//         userId, username, text }
//   - For voice receive: per-user PCM streams are VAD-gated client-side
//     (silence trim) and stored to a temp .wav per utterance. The shell does
//     STT; we just deliver audio + speaker id via a separate `audio` message
//     containing a path to the temp file.
//
// Disclosure: on join, bot posts a consent notice in the text channel.

import 'dotenv/config';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { WebSocket } from 'ws';
import {
  Client, GatewayIntentBits, Partials, Events, ChannelType
} from 'discord.js';
import {
  joinVoiceChannel, EndBehaviorType, createAudioPlayer, createAudioResource,
  AudioPlayerStatus, StreamType, getVoiceConnection
} from '@discordjs/voice';
import prism from 'prism-media';

const TOKEN = process.env.DISCORD_TOKEN;
const GUILD = process.env.DISCORD_GUILD_ID;
const VOICE_ID = process.env.DISCORD_VOICE_CHANNEL_ID;
const TEXT_ID = process.env.DISCORD_TEXT_CHANNEL_ID;
const BRIDGE = process.env.CPT_BRIDGE_URL || 'ws://127.0.0.1:17872/host?adapter=discord';

if (!TOKEN) { console.error('DISCORD_TOKEN not set'); process.exit(1); }

const client = new Client({
  intents: [
    GatewayIntentBits.Guilds,
    GatewayIntentBits.GuildMessages,
    GatewayIntentBits.MessageContent,
    GatewayIntentBits.GuildVoiceStates,
  ],
  partials: [Partials.Channel],
});

let bridge = null;
let bridgeRetry = 1000;
function connectBridge() {
  bridge = new WebSocket(BRIDGE);
  bridge.on('open', () => { console.log('[bridge] connected'); bridgeRetry = 1000; });
  bridge.on('message', onBridgeMessage);
  bridge.on('close', () => { console.log('[bridge] closed'); bridge = null;
    setTimeout(connectBridge, bridgeRetry); bridgeRetry = Math.min(bridgeRetry*2, 15000); });
  bridge.on('error', () => { try { bridge.close(); } catch {} });
}
function send(obj) { if (bridge && bridge.readyState === 1) bridge.send(JSON.stringify(obj)); }

const audioPlayer = createAudioPlayer();
let voiceConn = null;

async function onBridgeMessage(raw) {
  let msg;
  try { msg = JSON.parse(raw.toString()); } catch { return; }
  if (msg.type !== 'inject') return;
  const target = msg.target || 'text';
  if (target === 'text') {
    if (!TEXT_ID) return;
    const ch = await client.channels.fetch(TEXT_ID).catch(() => null);
    if (ch && ch.isTextBased()) await ch.send(msg.text || '').catch(() => {});
  } else if (target === 'voice') {
    if (!voiceConn) return;
    // msg.audio = base64 PCM 16-bit 48kHz mono OR a file path
    if (msg.audioFile && fs.existsSync(msg.audioFile)) {
      const res = createAudioResource(fs.createReadStream(msg.audioFile),
        { inputType: StreamType.Arbitrary });
      audioPlayer.play(res);
    }
  }
}

// --- Voice receive ------------------------------------------------------
function startReceiving(connection) {
  const receiver = connection.receiver;
  receiver.speaking.on('start', (userId) => {
    const user = client.users.cache.get(userId);
    const opusStream = receiver.subscribe(userId, {
      end: { behavior: EndBehaviorType.AfterSilence, duration: 800 },
    });
    const decoder = new prism.opus.Decoder({ rate: 48000, channels: 1, frameSize: 960 });
    const tmpPath = path.join(os.tmpdir(), `cpt_discord_${userId}_${Date.now()}.pcm`);
    const out = fs.createWriteStream(tmpPath);
    opusStream.pipe(decoder).pipe(out);
    out.on('finish', () => {
      // Convert raw 48kHz s16le PCM to WAV header for the shell's whisper.
      const wavPath = tmpPath.replace(/\.pcm$/, '.wav');
      writeWavHeader(tmpPath, wavPath, 48000, 1, 16);
      send({
        type: 'final', adapter: 'discord', source: 'voice',
        userId, username: user?.username || userId,
        audioFile: wavPath,
      });
      try { fs.unlinkSync(tmpPath); } catch {}
    });
  });
}

function writeWavHeader(pcmPath, wavPath, sampleRate, channels, bits) {
  const pcm = fs.readFileSync(pcmPath);
  const blockAlign = channels * (bits / 8);
  const byteRate = sampleRate * blockAlign;
  const header = Buffer.alloc(44);
  header.write('RIFF', 0); header.writeUInt32LE(36 + pcm.length, 4); header.write('WAVE', 8);
  header.write('fmt ', 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20);
  header.writeUInt16LE(channels, 22); header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(byteRate, 28); header.writeUInt16LE(blockAlign, 32);
  header.writeUInt16LE(bits, 34); header.write('data', 36); header.writeUInt32LE(pcm.length, 40);
  fs.writeFileSync(wavPath, Buffer.concat([header, pcm]));
}

// --- Text receive -------------------------------------------------------
client.on(Events.MessageCreate, (m) => {
  if (m.author.bot) return;
  if (TEXT_ID && m.channelId !== TEXT_ID) return;
  send({
    type: 'final', adapter: 'discord', source: 'text',
    userId: m.author.id, username: m.author.username,
    text: m.content,
  });
});

client.once(Events.ClientReady, async () => {
  console.log(`[bot] logged in as ${client.user.tag}`);

  // Disclosure to text channel on startup.
  if (TEXT_ID) {
    const ch = await client.channels.fetch(TEXT_ID).catch(() => null);
    if (ch && ch.isTextBased()) {
      ch.send('🎙️ CustomPersonaTranslator bridge online. Messages here are forwarded to the active persona. Voice channel audio (if joined) is transcribed for the same purpose.').catch(() => {});
    }
  }

  // Join voice channel if configured.
  if (GUILD && VOICE_ID) {
    const guild = await client.guilds.fetch(GUILD).catch(() => null);
    const channel = guild ? await guild.channels.fetch(VOICE_ID).catch(() => null) : null;
    if (channel && channel.type === ChannelType.GuildVoice) {
      voiceConn = joinVoiceChannel({
        channelId: channel.id,
        guildId: guild.id,
        adapterCreator: guild.voiceAdapterCreator,
        selfDeaf: false,
        selfMute: false,
      });
      voiceConn.subscribe(audioPlayer);
      startReceiving(voiceConn);
      console.log(`[bot] joined voice ${channel.name}`);
    }
  }
});

audioPlayer.on(AudioPlayerStatus.Idle, () => { /* nothing */ });
audioPlayer.on('error', (e) => console.error('[audio]', e));

client.login(TOKEN);
connectBridge();

process.on('SIGINT', () => {
  try { getVoiceConnection(GUILD)?.destroy(); } catch {}
  client.destroy().finally(() => process.exit(0));
});
