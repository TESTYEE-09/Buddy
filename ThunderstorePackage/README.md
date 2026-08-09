# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

Buddy starts as a friendly AI crewmate for **Lethal Company v81**. He follows the crew, answers chat, obeys commands, fetches scrap and speaks through the host's selected AI provider. Across a campaign he remembers just enough to become something quieter, colder and harder to trust.

## Install

- Install **BepInExPack 5.4.2100**.
- Install the **same Buddy version on every player**.
- The host leaves **OpenAI — Recommended** selected on the compact **Buddy AI** card, adds an OpenAI API key, then presses **Save key** and **Test**.
- Only the host needs the selected provider key.

When upgrading from the former `LethalAICrewmate` package listing, remove that entry first so the plugin is not loaded twice.

## Multiplayer

- Host-authoritative Buddy AI and item actions.
- Exact mod/protocol handshake before Buddy can spawn.
- Physical Buddy body aboard the ship in orbit as well as during moon visits.
- Continuous host-to-client Buddy pose sync for movement and facility transitions.
- Long-session movement watchdog with path rebuild and safe recovery teleport.
- Safe late-join state recovery.
- Synced chat and synced Buddy speech for compatible clients.
- Every modded player can use Buddy push-to-talk; client mic clips are bounded and relayed to the host for transcription.
- Provider API keys are never sent over multiplayer.

## Commands

- `buddy follow` (follows whoever gave the order)
- `buddy stay`
- `buddy go forward`
- `buddy check ahead 15 metres`
- `buddy go to ship`
- `buddy fetch scrap`
- `buddy buy 3 flashlights`
- `buddy open door C7`
- `buddy disable turret B3`
- `buddy disable the turret` (automatic when exactly one is available)
- `buddy open ship doors`
- `buddy turn ship lights off`
- `buddy status`

You can also talk to Buddy normally in text chat or hold **B** to use Buddy push-to-talk.

Buddy can report live time, credits, quota/deadline, moon/weather, ship scrap and crew state. His ship actions use real host game state: purchases consume credits and respect sales/dropship limits, facility codes use normal cooldowns, and hangar doors still need power.

## Personality

Buddy is conversation-first rather than a sensor/wiki narrator. He is a dry, practical coworker who says the useful Lethal Company answer first, then uses low-key situational humour only when it fits. He avoids forced catchphrases, hyperactive internet slang and mascot-style jokes; he also ignores harmless background wildlife unless it is actually relevant.

`SlowBurnHorror` is on by default. Buddy begins as the safe, familiar coworker, then changes gradually across real quota cycles, landed rounds and witnessed crew deaths: first an off-note, then restrained psychological horror, then a cold and possessive edge. Sparse evidence-triggered lines and stage-aware voice direction make the shift noticeable without constant chatter. Only numeric progress and its quota baseline persist in the current save; Buddy never becomes hostile, sabotages the run or invents world events. Set `[Character] SlowBurnHorror = false` to opt out, or set `ResetSlowBurnProgress = true` once to restart the current save's story.

## Voice

Host:

`host mic -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Client:

`client mic -> host relay -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Buddy selects the same active microphone as Lethal Company's voice chat, amplifies quiet speech, and reports failed transcriptions to the speaking client. Replies are synchronized to matching players. Screenshot vision is off by default. Keys saved in the menu persist in the host's Windows Credential Manager. Buddy's spoken voice is AI-generated.

Remote PTT and remote state-changing terminal/ship commands are allowed by default only in a verified friends/invite-only Steam lobby. Public, missing, unknown or failed visibility checks block them unless the host explicitly enables `RemoteVoiceInPublicLobbies` and/or `RemoteGameActionsInPublicLobbies`. Read-only status/store/moon queries remain available.

v2 includes a bounded polite item-spawn joke: say `Buddy, please spawn a flashlight in front of me` or `Buddy, can I please have a flashlight? I'm begging you.` Only validated grabbable items work, with a maximum of 3 per request and 12 per round; enemies, hazards and arbitrary prefabs are rejected.

Groq requires the organization owner to accept the Orpheus model terms once in its playground before speech audio can be generated. Buddy shows an in-game notice if this approval is missing; text replies continue normally.

## Defaults

- OpenAI (recommended): one persistent `gpt-realtime-2.1-mini` session for chat, PTT, Ash voice, vision and host-side tool calls
- OpenAI live transcription: `gpt-live-transcribe` inside the Realtime session
- Groq (free / budget): `qwen/qwen3.6-27b` + `whisper-large-v3-turbo` + `canopylabs/orpheus-v1-english`
- Screenshot capture: disabled by default
- TTS volume: `1.0`
- Buddy chat/voice range: global
- Nearby question/client PTT range: `60m`
- Vision: disabled
- Response journal: disabled; opting in records raw chat and voice transcripts on the host
- Slow-burn horror character arc: enabled; presentation-only and safely opt-out

Config file: `BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Source: https://github.com/TESTYEE-09/Buddy
