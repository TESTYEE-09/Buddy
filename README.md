# LethalAICrewmate

Buddy is an AI crewmate for **Lethal Company v81**. He joins the crew as a friendly networked Masked, follows players, answers chat, takes simple orders, fetches scrap and speaks with Groq TTS.

## Install

1. Install **BepInExPack 5.4.2100** for Lethal Company.
2. Install the same `LethalAICrewmate` release on **every player** in the lobby.
3. Put `LethalAICrewmate.dll` in `BepInEx/plugins/LethalAICrewmate/`, or install the release ZIP with a compatible mod manager.
4. Launch Lethal Company.
5. The host pastes a Groq API key into the **Lethal AI Crewmate — Groq** box on the main menu, presses **Save**, then **Test**.
6. Host a lobby and land on a moon. Buddy spawns once every connected player passes the mod compatibility handshake.

Only the host needs a Groq key. Prefer the host environment variable or the session-only menu field; the key is never sent to other players.

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
| `buddy follow` | Follow a living player |
| `buddy stay` | Hold position |
| `buddy go to ship` | Return toward the ship |
| `buddy fetch scrap` | Find nearby scrap and bring it back |

You can also talk to Buddy normally or ask him a question near him.

## Personality

Buddy is conversation-first: he responds to what players actually say instead of dumping sensor/entity facts. Harmless wildlife such as Manticoils and Roaming Locusts is treated as background unless the player asks about it.

Buddy can very rarely make a subtle fourth-wall joke when the moment fits. The rare beat is rate-limited in game code so it stays surprising instead of becoming his gimmick.

## Voice

**Every modded player** can hold **B** by default to talk to Buddy.

Host path:

`host mic -> Groq Whisper -> Buddy reply -> Groq Orpheus -> synced Buddy voice`

Client path:

`client mic -> bounded/chunked relay to host -> host Groq Whisper -> Buddy reply -> synced Buddy voice`

Clients do not need a Groq key. Remote microphone audio is captured only while the player holds the Buddy push-to-talk key, is size/rate limited, and is accepted only from connected matching clients. The stock nearby PTT range is 60m.

v1.4.7 adaptively amplifies quiet microphones before Whisper, keeps literal silence blocked, and makes Buddy replies global so every matching player receives both text and speech. If Windows selects the wrong microphone, set `[Voice] InputDevice` to its full name or a unique part of the name.

## Groq setup

New installs default to:

```ini
[Groq]
ApiKey =
Model = qwen/qwen3.6-27b
SttModel = whisper-large-v3-turbo
TtsModel = canopylabs/orpheus-v1-english
TtsVoice = austin
TtsEnabled = true
TtsDirection = friendly
TtsVolume = 1
```

For a persistent key without writing it to the BepInEx config, set the host-machine environment variable `LETHAL_AI_GROQ_API_KEY` before launching Steam. Keys entered through the main-menu panel are session-only by default.

Vision is enabled by default but captures the host screen only for clear visual questions such as “what am I looking at?” Normal conversation sends no screenshot. The default vision model is `qwen/qwen3.6-27b`.

```ini
[Vision]
Enabled = true
Model = qwen/qwen3.6-27b
```

The main-menu **Test** button validates the host key against Groq before a lobby starts.

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
MaxRecordSeconds = 8
InputDevice =
```

```ini
[Security]
PersistApiKey = false
AllowRemoteVoice = true
```

`AllowRemoteVoice = true` lets matching friends send tightly bounded PTT audio to the host; turn it off in public lobbies. `PersistApiKey = true` writes the key in plaintext to the BepInEx config; leave it off when using the environment variable.

`ChatHearRange = 0` makes Buddy chat/voice global. `ChatTriggerRange = 0` makes nearby unaddressed questions range-unlimited; explicit client Buddy PTT already works at any distance.

The old 70m reply default automatically migrates to global delivery. Other custom distance values are retained.

## Privacy and API usage

- `LETHAL_AI_GROQ_API_KEY` is the preferred persistent host-key source. Main-menu keys are session-only by default; plaintext config persistence is an explicit opt-in.
- The key is never included in multiplayer messages.
- Host push-to-talk audio goes directly to Groq when the host uses the Buddy voice key.
- Client push-to-talk audio is relayed only while that client holds the Buddy voice key; the host can disable remote audio for public lobbies.
- If Vision is enabled, a screenshot of the host view can be attached to a Groq chat request.
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
