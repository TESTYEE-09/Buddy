# LethalAICrewmate

Buddy is an AI crewmate for **Lethal Company v81**. He joins the crew as a friendly networked Masked, follows players, answers chat, takes simple orders, fetches scrap and can speak with Groq TTS.

## Install

1. Install **BepInExPack 5.4.2100** for Lethal Company.
2. Install the same `LethalAICrewmate` release on **every player** in the lobby.
3. Put `LethalAICrewmate.dll` in `BepInEx/plugins/LethalAICrewmate/`, or install the release ZIP with a compatible mod manager.
4. Launch Lethal Company.
5. The host pastes a Groq API key into the **Lethal AI Crewmate — Groq** box on the main menu, presses **Save**, then **Test**.
6. Host a lobby and land on a moon. Buddy spawns once every connected player passes the mod compatibility handshake.

Only the host needs a Groq key. The key stays in the host's local BepInEx config and is never sent to other players.

## Multiplayer safety

LethalAICrewmate is host-authoritative.

- Every peer registers the mod networking handlers automatically.
- Clients handshake with the host using an exact mod-version + wire-protocol check.
- Buddy does **not** spawn while any connected player is unmodded, still loading the mod, or running an incompatible version.
- If an incompatible player joins during a round, Buddy is despawned until the lobby is safe again.
- Clients accept Buddy state only from the server.
- Late joiners request the current Buddy ID and held-item state.
- Held-item sync is retried briefly if the client's network objects are still spawning.
- Buddy chat is replicated to compatible clients.
- Buddy TTS is generated once by the host, downsampled, chunked and replicated to compatible clients. Clients do not need a Groq key.

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

## Voice

The **host** can hold **V** by default to talk to Buddy:

`host mic -> Groq Whisper -> Buddy reply -> Groq Orpheus -> synced Buddy voice`

Normal text chat works for every player.

## Groq setup

New installs default to the production chat model:

```ini
[Groq]
ApiKey =
Model = llama-3.3-70b-versatile
SttModel = whisper-large-v3-turbo
TtsModel = canopylabs/orpheus-v1-english
TtsVoice = troy
TtsEnabled = true
TtsDirection = nervous
```

Optional vision is off by default for reliability and cost. To enable it, choose a vision-capable Groq model such as `qwen/qwen3.6-27b` and set:

```ini
[Vision]
Enabled = true
```

The main-menu **Test** button validates the host key against Groq before a lobby starts.

## Other config

```ini
[Crewmate]
Name = Buddy
Enabled = true
ChatHearRange = 25
ChatTriggerRange = 25
ObservationIntervalSeconds = 0

[Voice]
Enabled = true
PushToTalkKey = V
MaxRecordSeconds = 8
```

`ChatHearRange = 0` makes Buddy chat/voice global instead of proximity-based.

## Privacy and API usage

- The Groq key is stored locally in `BepInEx/config/com.lethalaicrewmate.buddy.cfg` on the host.
- The key is never included in multiplayer messages.
- Host push-to-talk audio is sent to Groq only when the host uses the voice key.
- If Vision is enabled, a screenshot of the host view can be attached to a Groq chat request.
- Generated Buddy speech is sent from the host to compatible clients as downsampled PCM audio.

## Build

The project targets `netstandard2.1` and is pinned to `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.

```bash
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
```

`pack.ps1` builds the DLL and creates the Thunderstore ZIP.

GitHub Actions also performs release checks, compiles with warnings treated as errors, checks for Groq-key-shaped secrets in source and the compiled DLL, validates package/version consistency, creates a SHA-256 checksum and uploads the ready-to-install ZIP.

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
