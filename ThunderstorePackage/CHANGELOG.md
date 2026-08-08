# Changelog

## 1.4.0

- Added exact multiplayer version/protocol handshakes.
- Buddy now waits to spawn until every connected remote player has the same compatible mod build.
- If an unmodded or mismatched player joins mid-round, Buddy despawns until the lobby is compatible again.
- Late joiners recover Buddy identity and held-item state.
- Held-item visual sync retries while client network objects finish spawning.
- Buddy TTS is generated once on the host and replicated to compatible clients as chunked 16 kHz mono audio.
- Groq API key remains host-only and is never sent to clients.
- Main-menu Groq panel now has Save, Test and Clear controls.
- New installs use production `llama-3.3-70b-versatile` for core chat; Qwen 3.6 remains an optional vision model.
- Vision is off by default for reliability and API cost.
- Removed the old shared/default Groq key from active source/config defaults.
- Removed tracked release ZIP/DLL binaries from the source tree; CI now produces release artifacts.
- Added release/version/package/secret checks and warnings-as-errors builds.

## 1.3.0

- Added main-menu Groq API-key entry.
- Added client network-handler registration and late-join handshake/state sync.
- Added server-only validation for Buddy custom state messages.

## 1.2.1

- Qwen chat, live sensors and optional vision.
- Terminal route/buy support.
- Orpheus TTS and Whisper STT hardening.
- Facility/exterior follow improvements and Buddy name tags.

## 1.1.2

- Private friends build. Retired because it contained a shared Groq key.

## 1.1.1

- Added Orpheus TTS and Groq chat model configuration.

## 1.1.0

- Spawn reliability and Groq Whisper push-to-talk.

## 1.0.1 / 1.0.0

- Initial Masked crewmate and OpenRouter-era prototype.
