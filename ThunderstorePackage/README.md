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
- Buddy automatically stays disabled if an unmodded or mismatched client is present.
- Safe late-join state recovery.
- Synced chat and synced Buddy speech for compatible clients.
- Groq keys are never sent over multiplayer.

## Commands

- `buddy follow`
- `buddy stay`
- `buddy go to ship`
- `buddy fetch scrap`

You can also talk to Buddy normally in text chat.

## Voice

The host can hold **V** by default:

`host mic -> Whisper -> Buddy reply -> Orpheus TTS -> synced Buddy voice`

## Defaults

- Chat: `llama-3.3-70b-versatile`
- STT: `whisper-large-v3-turbo`
- TTS: `canopylabs/orpheus-v1-english`
- Vision: off by default; opt in with a vision-capable model such as `qwen/qwen3.6-27b`

Config file: `BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Source: https://github.com/TESTYEE-09/LethalAICrewmate
