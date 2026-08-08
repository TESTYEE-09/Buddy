# LethalAICrewmate

Buddy is an AI crewmate for **Lethal Company v81**. He follows the crew, responds in chat, can stay/return/fetch scrap, and can use Groq for chat, Whisper speech-to-text and Orpheus TTS.

## Install

1. Install BepInExPack 5.4.2100.
2. Install this package for **every player in the lobby**.
3. Launch Lethal Company.
4. The host pastes a Groq API key into the small **Lethal AI Crewmate — Groq** box on the main menu and presses **Save**.
5. Host a lobby and land on a moon.

Only the host needs a Groq key. It is stored locally in `BepInEx/config/com.lethalaicrewmate.buddy.cfg` and is not sent to clients.

## Multiplayer

- Host-authoritative Buddy AI and item actions.
- Same mod version recommended/required across the lobby.
- Automatic host/client protocol handshake.
- Late-join state sync for Buddy and held items.
- Clients accept Buddy state only from the server.
- Buddy chat text is synced to modded clients.
- Groq TTS playback is currently host-local.

## Commands

| Chat | Effect |
| --- | --- |
| `buddy follow` | Follow a living player |
| `buddy stay` | Hold position |
| `buddy go to ship` | Return toward the ship |
| `buddy fetch scrap` | Find and deliver scrap |

## Voice

The host can hold **V** by default:

`microphone -> Groq Whisper -> Buddy response -> Groq Orpheus TTS`

## Config

```ini
[Groq]
ApiKey =
Model = qwen/qwen3.6-27b
SttModel = whisper-large-v3-turbo
TtsModel = canopylabs/orpheus-v1-english
TtsVoice = troy
TtsEnabled = true
TtsDirection = nervous
```

Source and issues: https://github.com/TESTYEE-09/LethalAICrewmate
