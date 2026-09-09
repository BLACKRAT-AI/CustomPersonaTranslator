# CustomPersonaTranslator

**A personal, experimental Windows project. Use at your own risk.**

A compact floating voice assistant with selectable personas, local voice cloning, hologram effects, and coding-agent integration. Supported agent tools can interact with your screen, mouse and keyboard.

## Open the app

Download or clone the complete repository, then double-click **CPT.exe**. Keep the `dev` folder next to it: the launcher opens the packaged application in `dev/app`. Downloading only the small launcher is not enough.

```text
CPT.exe       Start the app
README.md     This guide
dev/          Source, tests, build scripts, and packaged app
```

Requires Windows x64 and WebView2. The packaged app includes the .NET runtime. Speech tools, Python, and model weights download separately during setup; they are not included in Git. A supported coding CLI and its sign-in are required for agent requests.

On first use, configure the agent in Settings, set its trigger phrases, and choose a persona. Voice-clone settings offer **Original Chatterbox** and **Chatterbox Turbo**. Use **Listen to reference** and **Preview voice** to compare your clips with generated speech. Turbo can generate speech faster, but speed and voice likeness vary by hardware and reference audio. First model loading takes longer.

Your settings, imported clips, and saved personas live in `%LOCALAPPDATA%\CustomPersonaTranslator`. Updating the program does not require deleting that folder. The sample personas in the repository are separate from your personal saved settings.

## Personal project / use at your own risk

This is a personal project, not a supported commercial product. It is provided **as-is, without warranties or guarantees** of reliability, accuracy, voice likeness, response speed, or fitness for a particular purpose. Bugs may cause failed requests, incorrect actions, interrupted audio, or unwanted changes. Back up important files and supervise agents that have computer access. You are responsible for the permissions you grant and the actions you run.

Speech recognition and cloning run locally. Agent requests and persona rewrites are sent to the coding service you configure; screen content may be sent when using computer tools. Optional downloads and research use the network. Do not submit sensitive material unless you intend to share it with that service.

This project is not affiliated with or endorsed by the AI providers, character owners, or other third parties referenced by sample personas. Third-party software and assets retain their respective terms. Use only recordings and assets you have permission to use.

## Development

Install the .NET 10 SDK. From `dev`:

```powershell
dotnet build CustomPersonaTranslator.slnx
dotnet test tests/CPT.Tests
node --experimental-default-type=module tests/head_framing.test.mjs
powershell -File scripts/build-root.ps1
```

The last command rebuilds both the top-level launcher and `dev/app`. Close the packaged app before replacing its executable. Downloaded models, local runtimes, logs, and ordinary build output are excluded from Git.

See [development notes](dev/README.md) and [change notes](dev/docs/) for implementation details and known limitations. Passing automated checks does not guarantee that every desktop action or voice request will succeed.
