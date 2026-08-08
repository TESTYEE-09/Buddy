# LethalAICrewmate

Buddy is a friendly AI crewmate for **Lethal Company v81**. He follows the crew, answers chat, obeys commands, fetches scrap and speaks through the host's selected AI provider.

## Install

- Install **BepInExPack 5.4.2100**.
- Install the **same LethalAICrewmate version on every player**.
- The host adds an OpenAI API key from the small main-menu panel and presses **Test**.
- Only the host needs the selected provider key.

## Multiplayer

- Host-authoritative Buddy AI and item actions.
- Exact mod/protocol handshake before Buddy can spawn.
- Physical Buddy body aboard the ship in orbit as well as during moon visits.
- Continuous host-to-client Buddy pose sync for movement and facility transitions.
- Long-session movement watchdog with path rebuild and safe recovery teleport.
- Safe late-join state recovery.
- Synced chat and synced Buddy speech for compatible clients.
- Every modded player can use Buddy push-to-talk; client mic clips are bounded and relayed to the host for transcription.
- Groq keys are never sent over multiplayer.

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

## Voice

Host:

`host mic -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Client:

`client mic -> host relay -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Buddy selects the same active microphone as Lethal Company's voice chat, amplifies quiet speech, and reports failed transcriptions to the speaking client. Replies are synchronized to matching players. Screen capture is disabled. Keys saved in the menu persist in the host's Windows Credential Manager. Public-lobby hosts can set `[Security] AllowRemoteVoice = false`.

v2 includes a bounded polite item-spawn joke: say `Buddy, please spawn a flashlight in front of me` or `Buddy, can I please have a flashlight? I'm begging you.` Only validated grabbable items work, with a maximum of 3 per request and 12 per round; enemies, hazards and arbitrary prefabs are rejected.

Groq requires the organization owner to accept the Orpheus model terms once in its playground before speech audio can be generated. Buddy shows an in-game notice if this approval is missing; text replies continue normally.

## Defaults

- Buddy brain: `gpt-5.6-luna` through Responses with low reasoning, low verbosity and Fast service tier
- Native PTT voice: `gpt-realtime-2.1-mini` with Ash, 24 kHz PCM, far-field noise reduction, low reasoning, persistent WebSocket audio and host-side tool calls
- Screenshot capture: disabled; stock Buddy is text-only
- STT: `gpt-realtime-whisper`
- TTS: `gpt-4o-mini-tts`
- Fallback TTS voice: `ash` at natural playback speed
- TTS volume: `1.0`
- Buddy chat/voice range: global
- Nearby question/client PTT range: `60m`
- Vision: disabled

Config file: `BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Source: https://github.com/TESTYEE-09/LethalAICrewmate
