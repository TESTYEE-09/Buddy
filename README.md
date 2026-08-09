# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

Buddy starts as a friendly AI crewmate for **Lethal Company v81**. He joins as a networked Masked, follows players, answers chat, takes useful orders, fetches scrap and speaks through a host-selected AI provider. As the campaign continues, he remembers the shifts and gradually becomes harder to trust.

## Install

1. Install **BepInExPack 5.4.2100** for Lethal Company.
2. Install the same **Buddy** release on **every player** in the lobby.
3. Put `LethalAICrewmate.dll` in `BepInEx/plugins/Buddy/`, or install the release ZIP with a compatible mod manager. The legacy DLL filename is retained so existing installs and configs upgrade safely.
4. Launch Lethal Company.
5. The host leaves **OpenAI — Recommended** selected on the **Buddy AI** card, pastes an OpenAI API key, then presses **Save key** and **Test**.
6. Host a lobby. Buddy appears physically in the ship once every connected player passes the mod compatibility handshake, including while in orbit.

Only the host needs an API key. The main-menu Save button keeps it in that Windows user's Credential Manager between sessions; the key is never sent to other players.

## Multiplayer safety and sync

Buddy is host-authoritative.

- Every peer registers the mod networking handlers automatically.
- Clients handshake with the host using an exact mod-version + wire-protocol check.
- Buddy does **not** spawn while any connected player is unmodded, still loading the mod, or running an incompatible version.
- Clients accept Buddy state only from the server.
- Buddy's position, rotation and indoor/outdoor state are continuously replicated from the host, including facility transitions and recovery teleports.
- Late joiners recover Buddy identity and held-item state.
- Buddy chat and speech are replicated to compatible clients.
- The host generates TTS once and distributes bounded PCM audio; clients never receive the provider key.
- A host-side movement watchdog rebuilds stalled NavMesh paths and can safely recover Buddy beside his follow target after a persistent stall.
- Buddy normally catches up by walking with variable speed, natural spacing and restrained turning. Teleportation is reserved for persistent navigation failure after repeated path rebuilds.
- If his followed player dies, Buddy hesitates instead of snapping to another target. He only reports a nearby same-area death with line of sight, and reaches the next crewmate by normal navigation.
- Sparse contextual conversation can begin after real events such as entering or leaving the facility, returning to the ship, prolonged travel, separation, valuable scrap or long quiet downtime. Recent player speech takes priority.
- Fetch routines choose useful nearby scrap rather than blindly selecting the closest object. Say `buddy bring me scrap` for a personal handoff; the established `buddy fetch scrap` command delivers safely to the ship.
- At a closed door near his crewmate, Buddy pauses briefly instead of shoving into it. He never unlocks doors or gains extra terminal authority from this routine.

For multiplayer, **all players must use the same release**.

If upgrading from the former `LethalAICrewmate` package name, remove that old mod-manager entry before installing **Buddy** so two copies of the same plugin cannot load.

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
| `buddy bring me scrap` | Find useful nearby scrap and return it to you |
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

The recommended OpenAI experience uses one `gpt-realtime-2.1-mini` session for conversation, native voice, image questions and tool calling. Screenshot vision remains disabled by default and only captures one current host screenshot for an explicit visual question.

## Personality

Buddy is a dry, practical coworker: he says the useful Lethal Company answer first, then only adds low-key, situational humour when it fits. He avoids forced catchphrases, hyperactive internet slang, and mascot-style jokes.

Buddy is conversation-first: he responds to what players actually say instead of dumping sensor/entity facts. Harmless wildlife such as Manticoils and Roaming Locusts stays background unless the player asks about it.

By default, Buddy now has a slow-burn horror arc. He starts completely ordinary and trustworthy. Across fulfilled quotas, landed rounds and deaths he actually witnesses, his humor develops small off-notes, his attachment becomes uncomfortable, and his voice grows calmer and colder. Sparse character beats occur only after real game events and are separated by at least 150 seconds, so this plays as a campaign story rather than constant spooky chatter.

The arc stores only numeric progress and its quota baseline in the current Lethal Company save—never chat, transcripts or personal facts. It changes presentation only: Buddy remains neutralized, useful and host-authoritative, and never attacks, sabotages, fabricates sensor events or encourages a lethal decision. Set `[Character] SlowBurnHorror = false` to keep the ordinary coworker personality throughout. Set `ResetSlowBurnProgress = true` once to restart the current save's story; it automatically returns to false.

## Voice

**Every modded player** can hold **B** by default to talk to Buddy.

Host path:

`host mic -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Client path:

`client mic -> bounded/chunked relay to host -> GPT-Realtime-2.1 mini native speech-to-speech (Ash) -> synced Buddy voice`

Clients do not need a provider key. Remote microphone audio is captured only while the player holds the Buddy push-to-talk key, is size/rate limited, and is accepted only from connected matching clients. The stock nearby PTT range is 60m.

Buddy uses the same active microphone as Lethal Company's normal Dissonance voice chat, adaptively amplifies quiet speech, and shows the speaking client when transcription could not understand a clip. If an explicit override is needed, set `[Voice] InputDevice` to the device's full name or a unique part of it.

## AI setup

The main-menu setup card defaults to **OpenAI — Recommended**. It also offers **Groq — Free / budget** as a clearly separate option. New installs use:

```ini
[AI]
Provider = OpenAI

[Groq]
TtsVoice = austin
TtsDirection = friendly
```

The main-menu Save button persists the selected provider key in Windows Credential Manager for that Windows user. `LETHAL_AI_OPENAI_API_KEY` and `LETHAL_AI_GROQ_API_KEY` still take precedence when set before launching Steam.

OpenAI is one persistent `gpt-realtime-2.1-mini` WebSocket: it receives typed or 24 kHz PTT input, reasons, calls the bounded host tool when required, and produces Buddy's Ash voice. `gpt-live-transcribe` supplies live input transcripts inside that Realtime pipeline. There is no separate OpenAI chat model or request-based TTS fallback.

Groq is independent: `qwen/qwen3.6-27b` handles conversation, `whisper-large-v3-turbo` handles speech recognition, and `canopylabs/orpheus-v1-english` generates speech. Switching providers never sends one provider's key or model IDs to the other.

On upgrade, old model fields are ignored and disappear from the active settings UI. Buddy preserves provider choice, secure keys, voice preference, gameplay settings, security limits and character-arc progress.

Screenshot capture is off by default. If the host explicitly enables it, only an explicit visual question captures one current host screenshot for the selected provider; screenshots are never sent to clients.

v2 also supports a bounded joke/admin command: `Buddy, please spawn 2 flashlights in front of me`. Natural pleaded phrasing also works, for example: `Buddy, can I please have a flashlight? I'm begging you.` The requester must explicitly say please or beg; only validated grabbable item prefabs are allowed, quantities are capped at 3, and the lobby is capped at 12 spawned objects per round. Enemies, hazards, arbitrary prefabs and unknown names are rejected.

```ini
[Vision]
Enabled = false
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
SpokenReplies = true
Volume = 1
PushToTalkKey = B
AlternatePushToTalkKey = V
MaxRecordSeconds = 8
InputDevice =
```

```ini
[Security]
AllowRemoteVoice = true
RemoteVoiceInPublicLobbies = false
RemoteGameActionsInPublicLobbies = false

[Logging]
SaveResponses = false

[Character]
SlowBurnHorror = true
ResetSlowBurnProgress = false
```

`AllowRemoteVoice = true` permits the relay, but remote PTT is accepted by default only when Steam visibility is positively identified as friends/invite-only. Public, missing, unknown or failed visibility checks are blocked unless `RemoteVoiceInPublicLobbies = true`. Remote purchases, routes, item spawning and ship/facility changes use the same fail-closed rule unless `RemoteGameActionsInPublicLobbies = true`; read-only status/store/moon queries remain available. Menu keys are saved in Windows Credential Manager.

`SaveResponses = false` is the privacy-safe default. Enabling it writes raw player chat, voice transcripts, Buddy replies and confirmed tool results to the host-only `BepInEx/LethalAICrewmate-responses.log`. Existing configs migrate to off; re-enable it only when players understand the log.

`ChatHearRange = 0` makes Buddy chat/voice global. `ChatTriggerRange = 0` makes nearby unaddressed questions range-unlimited; explicit client Buddy PTT already works at any distance.

The old 70m reply default automatically migrates to global delivery. Other custom distance values are retained.

## Privacy and API usage

- `LETHAL_AI_OPENAI_API_KEY` and `LETHAL_AI_GROQ_API_KEY` remain optional host-key sources and override the selected provider's menu-saved key. Menu keys are stored in Windows Credential Manager.
- The key is never included in multiplayer messages.
- Host push-to-talk audio goes to the selected speech provider when the host uses the Buddy voice key.
- Client push-to-talk audio is relayed only while that client holds the Buddy voice key; the host can disable remote audio for public lobbies.
- Host screenshots are captured only when `[Vision] Enabled = true` (default off) and only for explicit visual questions; they are never transmitted to clients.
- Generated Buddy speech is sent from the host to compatible clients as downsampled PCM audio.
- Buddy's spoken voice is AI-generated.

## Build

The project targets `netstandard2.1` and is pinned to `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.

```bash
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
```

`pack.ps1` builds the DLL and creates the Thunderstore ZIP.

GitHub Actions performs release checks, compiles with warnings treated as errors, checks for API-key-shaped secrets (Groq and OpenAI) in source and the compiled DLL, validates package/version consistency, creates a SHA-256 checksum and uploads the ready-to-install ZIP.

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
