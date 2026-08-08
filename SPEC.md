# LethalAICrewmate — v1.4.5 Design Spec

BepInEx 5 plugin for **Lethal Company v81**. Adds a friendly AI-driven crewmate NPC (default name **Buddy**) backed by Groq on the host.

## Release architecture

- One `netstandard2.1` BepInEx assembly: `LethalAICrewmate.dll`.
- Pinned game references: `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.
- Harmony patches only; no custom network prefab or asset bundle required.
- Buddy uses the game's registered `MaskedPlayerEnemy` NetworkObject, then the mod neutralizes the hostile Masked behaviour and drives a host-side state machine.
- **Host authoritative:** spawning, movement decisions, commands, item actions, LLM calls, STT and TTS generation happen on the host.
- **All multiplayer players must install the exact same release.** Buddy does not spawn until every connected remote player completes the exact version/protocol handshake.

## Multiplayer protocol

Protocol version is maintained in `NetMessenger.ProtocolVersion` and must be incremented when a wire format becomes incompatible.

Client outbound custom messages:

- `Hello`: local mod version + protocol only.

Server outbound custom messages:

- `Welcome`: host mod version/protocol + compatibility result.
- `CrewmateSync`: identify/remove the Buddy NetworkObject ID.
- `ItemAttach`: mirror held scrap visuals.
- `CrewmateChat`: Buddy name/text/position.
- `TtsStart` / `TtsChunk`: already-generated, downsampled Buddy speech.

Rules:

- Clients accept Buddy state only from `NetworkManager.ServerClientId` and only after a successful handshake.
- Host sends custom Buddy state only to compatible clients.
- Late joiners request current Buddy + held-item state through the handshake path.
- Item attach messages are retried briefly client-side while spawned objects are still becoming available.
- If an unmodded or incompatible client is present, Buddy stays disabled. If one joins mid-round, an active Buddy is despawned until the session is compatible again.
- The Groq API key is never part of a network message.

## Buddy body and AI

Buddy is a `MaskedPlayerEnemy` spawned by the host with `RoundManager.Instance.SpawnEnemyGameObject` when possible.

Neutralization:

- hide mask visuals,
- clear kill/chase targets,
- skip hostile Masked AI/update/kill paths for registered Buddy instances,
- apply a normal suit,
- retain the NavMeshAgent for movement,
- identify Buddy by network ID in `CrewmateRegistry`.

Host state machine:

- `FollowOwner`
- `Stay`
- `ReturnToShip`
- `FetchScrap`

The host polls for a valid landed state and retries spawning. `MultiplayerSpawnGate` also guards both the public spawn request and the internal spawn attempt so event/retry paths cannot bypass compatibility checks.

## Chat and commands

Server chat is observed on the host. Duplicate Harmony observations are deduped by **player ID + message + short time window**.

Deterministic movement commands include:

- `buddy follow`
- `buddy stay`
- `buddy go to ship`
- `buddy fetch scrap`

Questions can trigger a reply when addressed to Buddy, or when the player is within `ChatTriggerRange` and the message ends with `?`.

Explicit terminal and ship actions (`route`, quantity-aware `buy`, coded facility doors/hazards, hangar doors and ship lights) are parsed deterministically from player chat. **LLM output is never permitted to execute side effects.** Model-produced `[ROUTE:]`, `[BUY:]` and `[TERMINAL:]` tags are stripped without running them. Deterministic status queries expose player-visible time, credits, quota/deadline, moon/weather, ship scrap and crew state. Buddy maintains a networked physical body in the ship during orbit and moon phases; follow orders transfer ownership to the requesting living player.

## Groq

Host config section:

- `Groq.ApiKey`: empty by default; saved locally.
- `Groq.Model`: `qwen/qwen3.6-27b` multimodal production default.
- `Groq.SttModel`: `whisper-large-v3-turbo`.
- `Groq.TtsModel`: `canopylabs/orpheus-v1-english`.
- `Groq.TtsVoice`: `troy` by default.

The main-menu panel supports **Save / Test / Clear**. Test validates the key against Groq's models endpoint.

Vision is disabled by default. If the host opts in, use a Groq model that supports images, such as `qwen/qwen3.6-27b` while available.

LLM rules:

- live game sensor context is included,
- the model is instructed not to invent unseen enemies/hazards,
- replies are short,
- movement tags can be parsed for Buddy movement only,
- one request at a time with a bounded queue,
- no API work blocks the Unity main thread.

## Voice

Host push-to-talk defaults to **B**:

`host microphone -> Groq Whisper -> LLM -> Groq Orpheus -> Buddy speech`

TTS is generated exactly once on the host. The decoded clip is downmixed/downsampled to 16 kHz mono PCM, capped, chunked over reliable NGO named messages, rebuilt on clients and played locally near Buddy. Multiplayer clients do not make Groq calls.

## Scrap

Fetch mode finds valid unheld scrap, moves to it, mirrors a held visual on clients, returns toward the ship and drops it. Failures should fall back to dropping safely and returning to Follow.

## Privacy and security

- No API key default in source or binaries.
- Never log the API key.
- Never transmit the API key to clients.
- Legacy `[OpenRouter] ApiKey` migration only accepts a Groq-shaped `gsk_` key; other provider keys are ignored.
- Historical private builds contained a shared key; that historical key must be revoked/rotated externally.
- Generated DLLs and release ZIPs are not tracked in source.

## Release gates

GitHub Actions and `pack.ps1` enforce:

- manifest / csproj / `Plugin.ModVersion` equality,
- warnings-as-errors compilation,
- source scan for Groq-key-shaped secrets,
- compiled DLL scan for Groq-key-shaped secrets,
- exact Thunderstore package file whitelist,
- ZIP extraction/validation,
- SHA-256 checksum generation.

A version tag `vX.Y.Z` must match the package version; tag CI can publish the tested ZIP + checksum as a GitHub Release.

## Shipping package

Exactly:

- `LethalAICrewmate.dll`
- `manifest.json`
- `README.md`
- `CHANGELOG.md`
- `icon.png`
