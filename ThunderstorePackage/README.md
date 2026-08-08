# LethalAICrewmate

Buddy is a friendly AI crewmate for **Lethal Company v81**. He follows the crew, answers chat, obeys simple commands, fetches scrap and can speak with Groq TTS.

## Install

- Install **BepInExPack 5.4.2100**.
- Install the **same LethalAICrewmate version on every player**.
- The host adds a Groq API key from the small main-menu panel and presses **Test**.
- Only the host needs a Groq key.

## Multiplayer

- Host-authoritative Buddy AI and item actions.
- Exact mod/protocol handshake before Buddy can spawn.
- Physical Buddy body aboard the ship in orbit as well as during moon visits.
- Continuous host-to-client Buddy pose sync for movement and facility transitions.
- Long-session movement watchdog with path rebuild and safe recovery teleport.
- Safe late-join state recovery.
- Synced chat and synced Buddy speech for compatible clients.
- Every modded player can use Buddy push-to-talk; client mic clips are bounded and relayed to the host for Whisper transcription.
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
- `buddy open ship doors`
- `buddy turn ship lights off`
- `buddy status`

You can also talk to Buddy normally in text chat or hold **B** to use Buddy push-to-talk.

Buddy can report live time, credits, quota/deadline, moon/weather, ship scrap and crew state. His ship actions use real host game state: purchases consume credits and respect sales/dropship limits, facility codes use normal cooldowns, and hangar doors still need power.

## Personality

Buddy is conversation-first rather than a sensor/wiki narrator. His normal replies are short and more consistently dry/witty. He ignores harmless background wildlife unless it is actually relevant and can very rarely make a subtle fourth-wall joke without turning that into his whole personality.

## Voice

Host:

`host mic -> Whisper -> Buddy reply -> Orpheus TTS -> synced Buddy voice`

Client:

`client mic -> host relay -> host Whisper -> Buddy reply -> synced Buddy voice`

v1.4.8 selects the same active microphone as Lethal Company's voice chat, amplifies quiet speech, and reports failed transcriptions to the speaking client. It also delivers replies globally and automatically captures a clearer host screenshot for visual questions. New Groq keys entered in the menu are session-only by default; use `LETHAL_AI_GROQ_API_KEY` for persistent host setup. Public-lobby hosts can set `[Security] AllowRemoteVoice = false`.

Groq requires the organization owner to accept the Orpheus model terms once in its playground before speech audio can be generated. Buddy shows an in-game notice if this approval is missing; text replies continue normally.

## Defaults

- Chat and visual questions: `qwen/qwen3.6-27b`
- STT: `whisper-large-v3-turbo`
- TTS: `canopylabs/orpheus-v1-english`
- TTS voice: `austin`
- TTS direction: `friendly`
- TTS volume: `1.0`
- Buddy chat/voice range: global
- Nearby question/client PTT range: `60m`
- Vision: automatic for clear visual questions; 1280px JPEG at quality 72

Config file: `BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Source: https://github.com/TESTYEE-09/LethalAICrewmate
