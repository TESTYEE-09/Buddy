# LethalAICrewmate

Buddy is an AI crewmate for **Lethal Company v81**. He joins the crew as a friendly networked Masked, follows players, answers chat, takes simple orders, fetches scrap and speaks through a host-selected AI provider.

## Install

1. Install **BepInExPack 5.4.2100** for Lethal Company.
2. Install the same `LethalAICrewmate` release on **every player** in the lobby.
3. Put `LethalAICrewmate.dll` in `BepInEx/plugins/LethalAICrewmate/`, or install the release ZIP with a compatible mod manager.
4. Launch Lethal Company.
5. The host pastes an OpenAI API key into the **Lethal AI Crewmate - OpenAI** box on the main menu, presses **Save**, then **Test**.
6. Host a lobby. Buddy appears physically in the ship once every connected player passes the mod compatibility handshake, including while in orbit.

Only the host needs an API key. The main-menu Save button keeps it in that Windows user's Credential Manager between sessions; the key is never sent to other players.

## Multiplayer safety and sync

LethalAICrewmate is host-authoritative.

- Every peer registers the mod networking handlers automatically.
- Clients handshake with the host using an exact mod-version + wire-protocol check.
- Buddy does **not** spawn while any connected player is unmodded, still loading the mod, or running an incompatible version.
- Clients accept Buddy state only from the server.
- Buddy's position, rotation and indoor/outdoor state are continuously replicated from the host, including facility transitions and recovery teleports.
- Late joiners recover Buddy identity and held-item state.
- Buddy chat and speech are replicated to compatible clients.
- The host generates TTS once and distributes bounded PCM audio; clients never receive the Groq key.
- A host-side movement watchdog rebuilds stalled NavMesh paths and can safely recover Buddy beside his follow target after a persistent stall.

For multiplayer, **all players must use the same release**.

## Commands

Type these in normal Lethal Company chat:

| Command | Result |
| --- | --- |
| `buddy follow` | Follow the player who gave the order |
| `buddy stay` | Hold position |
| `buddy go forward` | Scout about 10 metres in the requesting player's facing direction, report, then return |
| `buddy check ahead 15 metres` | Scout a requested safe distance, clamped between 4m and 18m |
| `buddy go to ship` | Return toward the ship |
| `buddy fetch scrap` | Find nearby scrap and bring it back |
| `buddy buy 3 flashlights` | Buy store items using the real price, sale and crew credits |
| `buddy open door C7` | Open a facility door using its visible terminal code |
| `buddy disable turret B3` | Disable a coded turret/landmine through the terminal |
| `buddy disable the turret` | Disable it automatically when exactly one terminal-controlled turret exists |
| `buddy open ship doors` | Use the hangar-door controls when powered and available |
| `buddy turn ship lights off` | Control the ship-room lights |
| `buddy status` | Report time, credits, quota, deadline, moon, weather, scrap and crew |
| `buddy what time is it?` | Answer a specific live ship-status question without guessing |

You can also talk to Buddy normally or ask him a question near him.

Ship and terminal actions are host-authoritative and use the same game state as a player. Purchases respect sales, available credits and the 12-item dropship limit. Facility codes respect their normal cooldown, and ship doors still require working controls and hydraulic power.

The stock OpenAI configuration uses `gpt-5.6-luna` for conversation and keeps screenshot vision disabled.

## Personality

Buddy is conversation-first: he responds to what players actually say instead of dumping sensor/entity facts. v1.5.0 uses a substantially richer behavior prompt covering grounded multiplayer awareness, tool honesty, danger calibration, vision limits and natural dry humor. Harmless wildlife such as Manticoils and Roaming Locusts is treated as background unless the player asks about it.

Buddy can very rarely make a subtle fourth-wall joke when the moment fits. The rare beat is rate-limited in game code so it stays surprising instead of becoming his gimmick.

## Voice

**Every modded player** can hold **B** by default to talk to Buddy.

Host path:

`host mic -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Client path:

`client mic -> bounded/chunked relay to host -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Clients do not need a Groq key. Remote microphone audio is captured only while the player holds the Buddy push-to-talk key, is size/rate limited, and is accepted only from connected matching clients. The stock nearby PTT range is 60m.

v1.4.8 uses the same active microphone as Lethal Company's normal Dissonance voice chat, adaptively amplifies quiet speech, and shows the speaking client when Whisper could not understand a clip. If an explicit override is needed, set `[Voice] InputDevice` to the device's full name or a unique part of it.

## AI setup

New installs default to:

```ini
[AI]
Provider = OpenAI

[Groq]
ApiKey =
Model = gpt-5.6-luna
SttModel = gpt-realtime-whisper
TtsModel = gpt-4o-mini-tts
TtsVoice = ash
TtsEnabled = true
TtsDirection =
TtsVolume = 1

[OpenAI]
RealtimeVoiceModel = gpt-realtime-2.1-mini
```

The main-menu Save button persists the selected provider key in Windows Credential Manager for that Windows user. `LETHAL_AI_OPENAI_API_KEY` is still supported and takes precedence when set before launching Steam. Text chat uses `gpt-5.6-luna` through Responses with low reasoning, low verbosity and Fast service tier. Push-to-talk uses a persistent `gpt-realtime-2.1-mini` WebSocket with native PCM audio input/output, Ash voice, input transcription and host-side function calling. The separate `gpt-realtime-whisper` and `gpt-4o-mini-tts` settings remain available for non-native/fallback speech paths. The older Groq provider remains selectable with `[AI] Provider = Groq` and `LETHAL_AI_GROQ_API_KEY`.

Screenshot capture is disabled in v1.5.3. Stock Buddy is text-only and does not capture the host screen.

v2 also supports a bounded joke/admin command: `Buddy, please spawn 2 flashlights in front of me`. The requester must explicitly say please or beg; only validated grabbable item prefabs are allowed, quantities are capped at 3, and the lobby is capped at 12 spawned objects per round. Enemies, hazards, arbitrary prefabs and unknown names are rejected.

```ini
[Vision]
Enabled = false
Model = qwen/qwen3.6-27b
```

The main-menu **Test** button validates the selected provider key before a lobby starts.

Orpheus TTS also requires the Groq organization owner to accept that model's terms once. If Groq returns `model_terms_required`, open [the Orpheus playground](https://console.groq.com/playground?model=canopylabs%2Forpheus-v1-english), accept the terms, then restart the game. Text replies continue working without TTS.

## Other config

```ini
[Crewmate]
Name = Buddy
Enabled = true
ChatHearRange = 0
ChatTriggerRange = 60
ObservationIntervalSeconds = 0

[Voice]
Enabled = true
PushToTalkKey = B
AlternatePushToTalkKey = V
MaxRecordSeconds = 8
InputDevice =
```

```ini
[Security]
PersistApiKey = false
AllowRemoteVoice = true
```

`AllowRemoteVoice = true` lets matching friends send tightly bounded PTT audio to the host; turn it off in public lobbies. `PersistApiKey` is retained only for old config compatibility: menu keys are now saved in Windows Credential Manager, not in plaintext config.

`ChatHearRange = 0` makes Buddy chat/voice global. `ChatTriggerRange = 0` makes nearby unaddressed questions range-unlimited; explicit client Buddy PTT already works at any distance.

The old 70m reply default automatically migrates to global delivery. Other custom distance values are retained.

## Privacy and API usage

- `LETHAL_AI_GROQ_API_KEY` remains an optional persistent host-key source and overrides the menu-saved key. Menu keys are stored in Windows Credential Manager.
- The key is never included in multiplayer messages.
- Host push-to-talk audio goes directly to Groq when the host uses the Buddy voice key.
- Client push-to-talk audio is relayed only while that client holds the Buddy voice key; the host can disable remote audio for public lobbies.
- v1.5.3 does not capture or transmit host screenshots.
- Generated Buddy speech is sent from the host to compatible clients as downsampled PCM audio.

## Build

The project targets `netstandard2.1` and is pinned to `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.

```bash
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
```

`pack.ps1` builds the DLL and creates the Thunderstore ZIP.

GitHub Actions performs release checks, compiles with warnings treated as errors, checks for Groq-key-shaped secrets in source and the compiled DLL, validates package/version consistency, creates a SHA-256 checksum and uploads the ready-to-install ZIP.

Generated DLLs and release ZIPs are intentionally not committed to the source tree.

## Release ZIP

A shipping ZIP contains only:

- `LethalAICrewmate.dll`
- `manifest.json`
- `README.md`
- `CHANGELOG.md`
- `icon.png`

## Important security note

Older private builds contained a shared Groq key. That key must be revoked/rotated before public distribution. Current release source and generated packages do not intentionally include a default API key.
