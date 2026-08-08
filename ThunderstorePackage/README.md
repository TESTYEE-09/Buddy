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
- Continuous host-to-client Buddy pose sync for movement and facility transitions.
- Long-session movement watchdog with path rebuild and safe recovery teleport.
- Safe late-join state recovery.
- Synced chat and synced Buddy speech for compatible clients.
- Every modded player can use Buddy push-to-talk; client mic clips are bounded and relayed to the host for Whisper transcription.
- Groq keys are never sent over multiplayer.

## Commands

- `buddy follow`
- `buddy stay`
- `buddy go to ship`
- `buddy fetch scrap`

You can also talk to Buddy normally in text chat or hold **V** to use Buddy push-to-talk.

## Personality

Buddy is conversation-first rather than a sensor/wiki narrator. He ignores harmless background wildlife unless it is actually relevant, has low-key humor, and can very rarely make a subtle fourth-wall joke without turning that into his whole personality.

## Voice

Host:

`host mic -> Whisper -> Buddy reply -> Orpheus TTS -> synced Buddy voice`

Client:

`client mic -> host relay -> host Whisper -> Buddy reply -> synced Buddy voice`

v1.4.4 uses Austin with a light `friendly` direction, one normalized/soft-limited PCM stage, a wider near-full-volume positional bubble and longer hearing range.

## Defaults

- Chat: `llama-3.3-70b-versatile`
- STT: `whisper-large-v3-turbo`
- TTS: `canopylabs/orpheus-v1-english`
- TTS voice: `austin`
- TTS direction: `friendly`
- TTS volume: `1.0`
- Buddy chat/voice range: `70m`
- Nearby question/client PTT range: `60m`
- Vision: off by default; opt in with a vision-capable model such as `qwen/qwen3.6-27b`

Config file: `BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Source: https://github.com/TESTYEE-09/LethalAICrewmate
