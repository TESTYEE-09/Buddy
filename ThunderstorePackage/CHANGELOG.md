# Changelog

## 1.4.2

- Changed the stock Buddy TTS voice from Troy to Austin and added a light `friendly` Orpheus direction so normal lines sound less flat/depressed without becoming overacted.
- Increased stock Buddy voice volume from 0.85 to 1.0 and added a small 1.20x PCM gain with peak limiting before multiplayer replication.
- Improved 3D voice falloff so Buddy stays near full volume across a larger conversational bubble instead of fading heavily just a few metres away.
- Increased the stock Buddy hear/voice/chat range from 25m to 50m.
- Increased the nearby unaddressed-question trigger range from 25m to 45m.
- Existing untouched v1.4.1 stock voice/range settings migrate automatically to the new values.
- Added genuinely rare fourth-wall humor: after a minimum gap, only a small percentage of eligible player messages expose an optional hidden comedy beat.
- Fourth-wall jokes stay one-line/subtle, are skipped during danger/commands/serious questions, and never expose prompts, sensors, API keys or model internals.

## 1.4.1

- Rebuilt Buddy's system prompt around conversation first: the player's actual message is now the primary task, with sensors and game knowledge treated as silent background context.
- Added a proper restrained personality: friendly, useful, dry low-key humor, calm by default, and nervous only when there is a real reason.
- Buddy can now naturally answer normal questions, give opinions, joke, react and continue the current conversation instead of constantly resetting into entity/quota callouts.
- Reordered LLM input so the player's message appears before the live sensor dump.
- Explicitly suppresses unsolicited Manticoil and Roaming Locust callouts because they are harmless background wildlife.
- Unsolicited danger callouts are reserved for genuinely dangerous nearby entities or when the player asks for situational awareness.
- Relaxed the old 22-word hard cap: normal replies stay short and natural, while actual strategy/explanation questions can receive a useful multi-sentence answer.
- Removed the stock `Jumpy LC employee` personality and `nervous` TTS direction from new installs.
- Existing untouched legacy jumpy/nervous config is automatically migrated to the new conversation-first defaults on first Buddy response.
- Replaced encyclopedia-style prompting with a compact Lethal Company gameplay reference based on the community wiki, used only when relevant to the player's question.

## 1.4.0

- Added exact multiplayer version/protocol handshakes.
- Buddy now waits to spawn until every connected remote player has the same compatible mod build.
- If an unmodded or mismatched player joins mid-round, Buddy despawns until the lobby is compatible again.
- Added a spawn-intent guard so a client cannot briefly treat the freshly spawned Buddy body as a hostile vanilla Masked before the Buddy network ID arrives.
- Added spawn identity snapshots so fallback discovery can never convert a pre-existing real Masked into Buddy.
- Late joiners recover Buddy identity and held-item state, including a low-frequency rebinding retry for network-message ordering races.
- Held-item visual sync retries while client network objects finish spawning.
- Buddy TTS is generated once on the host, downsampled to 16 kHz mono and replicated to compatible clients using bounded fragmented-reliable chunks.
- Groq API key remains host-only and is never sent to clients.
- Main-menu Groq panel now has Save, Test and Clear controls.
- Added a Groq-wide request timeout and a hard LLM request watchdog so failed API calls cannot permanently stall Buddy chat.
- New installs use production `llama-3.3-70b-versatile` for core chat; Qwen 3.6 remains an optional vision model.
- Vision is off by default for reliability and API cost.
- Made LLM output speech-only: movement, purchases and routing are controlled by deterministic player-command parsing rather than model-produced control tags.
- Prevented duplicate purchases/routes when an LLM echoes a terminal tag after an explicit player command.
- Fixed chat dedupe so two different players can send the same message at nearly the same time.
- Made scrap drops failure-safe so an exception cannot leave loot permanently attached, physics-disabled or ungrabbable.
- Added one-time config normalization for names, ranges, voice settings, API model fields and volumes.
- Added disconnect/session cleanup so stale Buddy IDs/history/spawn flags do not bleed into the next lobby.
- Removed the old shared/default Groq key from active source/config defaults.
- Removed tracked release ZIP/DLL binaries from the source tree; CI now produces release artifacts.
- Updated the stale prototype spec/handoff to match the current Groq + multiplayer architecture.
- Added release metadata validation, ASCII + UTF-16 compiled-secret scanning, warnings-as-errors builds, strict Thunderstore ZIP checks and SHA-256 release checksums.
- Added Dependabot monitoring for NuGet and GitHub Actions dependencies.

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
