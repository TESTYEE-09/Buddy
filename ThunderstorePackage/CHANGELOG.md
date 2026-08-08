# Changelog

## 1.3.0

- Added a simple masked Groq API-key field and Save button to the Lethal Company main menu.
- Removed the built-in/shared Groq key from new builds. The host keeps their own key in local BepInEx config.
- Hardened multiplayer around a host-authoritative Buddy: clients receive state but cannot directly author Buddy state through the mod's custom messages.
- Fixed client message-handler registration so every modded peer can receive Buddy sync/chat/item messages.
- Added a multiplayer protocol/version handshake with a visible mismatch warning.
- Added late-join sync for Buddy's network ID and currently held item.
- Late-bound clients now apply Buddy's friendly patches and name tag after his network object appears.
- Added a full install, multiplayer, commands, config and troubleshooting README.
- Added a reproducible GitHub Actions build that produces a ready-to-install Thunderstore ZIP.

## 1.2.1

- Default chat model moved to `qwen/qwen3.6-27b` with thinking hidden.
- Added live game sensor context and optional host-view vision.
- Added terminal route/buy support and hardened Orpheus TTS / Whisper STT model selection.
- Added facility/exterior follow handling, larger follow distance, name tags and Input System push-to-talk support.

## 1.1.2

- Private test build used a shared Groq key by default. This is removed in 1.3.0 and the old key should be rotated before distribution.

## 1.1.1

- Added Groq chat, Orpheus TTS and shorter Buddy replies.

## 1.1.0

- Spawn reliability; Groq chat + Whisper STT push-to-talk.

## 1.0.1 / 1.0.0

- Initial Masked crewmate + OpenRouter era.
