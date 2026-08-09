# Buddy — v3.0.0 Design Spec

BepInEx 5 plugin for **Lethal Company v81**. Adds a friendly AI-driven crewmate NPC (default name **Buddy**) backed by OpenAI (default) or Groq on the host.

## Release architecture

- One `netstandard2.1` BepInEx assembly: `LethalAICrewmate.dll`.
- Public product/package name: **Buddy**. The assembly filename, namespace, plugin GUID, wire message names, credential target, save keys and existing response-log filename intentionally retain legacy identifiers for upgrade and multiplayer compatibility.
- Pinned game references: `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.
- Harmony patches only; no custom network prefab or asset bundle required.
- Buddy uses the game's registered `MaskedPlayerEnemy` NetworkObject, then the mod neutralizes the hostile Masked behaviour and drives a host-side state machine.
- **Host authoritative:** spawning, movement decisions, commands, item actions, LLM calls, STT and TTS generation happen on the host.
- **All multiplayer players must install the exact same release.** Buddy does not spawn until every connected remote player completes the exact version/protocol handshake.

## Multiplayer protocol

Protocol version is maintained in `NetMessenger.ProtocolVersion` and must be incremented when a wire format becomes incompatible.

Client outbound custom messages:

- `Hello`: local mod version + protocol only.
- `VoiceStart` / `VoiceChunk`: bounded remote PTT upload to the host when security policy permits it.

Server outbound custom messages:

- `Welcome`: host mod version/protocol + compatibility result.
- `CrewmateSync`: identify/remove the Buddy NetworkObject ID.
- `ItemAttach`: mirror held scrap visuals.
- `CrewmateChat`: Buddy name/text/position.
- `TtsStart` / `TtsChunk`: already-generated, downsampled Buddy speech.
- `VoiceHint`: bounded relay/transcription feedback to the speaking client.

Rules:

- Clients accept Buddy state only from `NetworkManager.ServerClientId` and only after a successful handshake.
- Host sends custom Buddy state only to compatible clients.
- Late joiners request current Buddy + held-item state through the handshake path.
- Item attach messages are retried briefly client-side while spawned objects are still becoming available.
- If an unmodded or incompatible client is present at spawn time, Buddy stays disabled. A mid-round join gets the normal handshake grace window without replacing Buddy's network body; a confirmed mismatch or missing handshake after that window still despawns him fail-closed.
- The selected provider API key is never part of a network message.

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

Follow movement uses NavMeshAgent only—there is no raw-transform flight fallback. Buddy uses hysteresis, stable side offset, slower turning and distance-based catch-up speed. A stalled path is rebuilt repeatedly; emergency teleport recovery requires at least 20 seconds without progress, three rebuild attempts and either extreme separation or a persistent area mismatch. Facility/exterior mismatches receive three spaced path rebuild attempts and must persist for at least 20 seconds before last-resort sampled-NavMesh recovery.

When the current followed player dies, Buddy holds position for 8–12 seconds. Same-area proximity within 20 metres plus a clear line of sight is the minimum evidence that he witnessed it. He then chooses the nearest living player and follows by walking; only after reaching them can the autonomy director report the witnessed death.

## Chat and commands

Server chat is observed on the host. Duplicate Harmony observations are deduped by **player ID + message + short time window**.

Deterministic movement commands include:

- `buddy follow`
- `buddy stay`
- `buddy go to ship`
- `buddy fetch scrap`

Questions can trigger a reply when addressed to Buddy, or when the player is within `ChatTriggerRange` and the message ends with `?`.

Explicit terminal and ship actions (`route`, quantity-aware `buy`, coded facility doors/hazards, hangar doors and ship lights) are parsed deterministically from player chat. OpenAI Realtime can request the single bounded `execute_game_command` host tool, but the model never performs a side effect directly: the host re-parses, authorizes and executes the request through the same deterministic command layer. Model-produced `[ROUTE:]`, `[BUY:]` and `[TERMINAL:]` text tags are stripped without running them. Deterministic status queries expose player-visible time, credits, quota/deadline, moon/weather, ship scrap and crew state. Buddy maintains a networked physical body in the ship during orbit and moon phases; follow orders transfer ownership to the requesting living player.

Movement orders use one deterministic parser so overlapping conversational keywords cannot accidentally change state. Scout-ahead orders choose a complete reachable path 4-18 metres along the requester's facing direction, report nearby same-area threats or scrap, pause briefly, then return to `FollowOwner`. A blocked or stalled scout cancels safely instead of teleporting forward. Fetch selection uses a bounded value-versus-distance score. Personal `bring me` fetch phrasing returns the item near the living requester; ordinary fetch remains ship delivery. Closed-door detection only permits a short wait while the owner is nearby and never grants unlock, terminal or protected action authority.

## Slow-burn character arc

`[Character] SlowBurnHorror=true` enables a four-stage presentation arc: `Coworker`, `OffNote`,
`Unsettling`, and `Cold`. The host increments a numeric score only from confirmed game evidence:
fulfilled quota cycles, new landed round seeds, and deaths Buddy locally witnessed while following. Global drops in `StartOfRound.livingPlayers` are not knowledge. Thresholds
are deliberately slow (3, 8, and 15 points). Two integers are stored in the current Lethal Company
save: `LethalAICrewmate_CharacterArcProgress` and the last counted
`LethalAICrewmate_CharacterArcQuotaCycles`. The baseline prevents quota double-counting across reloads;
no player dialogue, transcript, name, or inferred personal fact is persisted. A one-shot
`ResetSlowBurnProgress=true` resets both values for the current save and automatically clears itself.

Stage changes tune the conversation and native voice policy. Sparse deterministic
lines may fire after a real round/quota/witnessed-death event, with a 150-second cooldown. Stage zero produces no
forced horror beats. Arc state may adjust only valid idle gaze and follow spacing for presentation; it
cannot cause invalid navigation, teleport recovery, combat, terminal, spawn, networking, visibility or
authorization changes. Buddy remains a neutralized companion at every stage.

## Contextual autonomy

A single host-side director may enqueue one grounded observation after facility entry/exit, returning
to ship, long travel, sustained separation, high-value loose scrap or long quiet downtime. It uses a
55-second global cooldown, per-event repetition limits and a 12-second player-priority window.
Observations are disposable and never stack ahead of player chat or PTT. The selected provider creates
the actual short line from live sensor context; deterministic danger warnings remain separate.

## AI providers

OpenAI is the recommended provider. A single persistent `gpt-realtime-2.1-mini` session handles typed
conversation, PTT audio, native Ash speech, image input and bounded host-side tool calls.
`gpt-live-transcribe` is the session's live input transcription model. There is no separate OpenAI
chat or request-based TTS path. Groq remains a fully functional secondary/free option.

Groq host config section:

- `Groq.TtsVoice`: `austin` by default.
- `Groq.TtsDirection`: `friendly` by default.

Provider model IDs are release-owned rather than user-facing config controls: Groq is pinned to
`qwen/qwen3.6-27b`, `whisper-large-v3-turbo` and `canopylabs/orpheus-v1-english`.

The main-menu panel supports **Save key / Test / Clear** for the selected provider.

Vision is disabled by default. OpenAI image questions stay in its Realtime session; Groq uses Qwen 3.6.

LLM rules:

- live game sensor context is included,
- the model is instructed not to invent unseen enemies/hazards,
- replies are short,
- model-produced movement/action tags are stripped; Realtime tool requests still pass through deterministic host parsing and authorization before game state changes,
- one request at a time with a bounded queue,
- no API work blocks the Unity main thread.

## Voice

Host push-to-talk defaults to **B**:

`host microphone -> selected STT/native Realtime path -> Buddy response -> synced AI-generated speech`

TTS is generated exactly once on the host. The decoded clip is downmixed/downsampled to 16 kHz mono PCM, capped, chunked over reliable NGO named messages, rebuilt on clients and played locally near Buddy. Multiplayer clients do not make Groq calls.

## Scrap

Fetch mode finds useful valid unheld scrap, moves to it, mirrors a held visual on clients, then returns either to the ship or—only for explicit personal handoff phrasing—to the requesting living player. Failures fall back to safe ship delivery or dropping safely and returning to Follow.

## Privacy and security

- No API key default in source or binaries.
- Never log the API key.
- Never transmit the API key to clients.
- Response journaling is opt-in and stores raw chat/transcripts only on the host; paired turns use explicit correlation IDs.
- Public or unverified lobby visibility fails closed for remote audio and state-changing game actions unless the host opts in.
- Legacy `[OpenRouter] ApiKey` migration only accepts a Groq-shaped `gsk_` key; other provider keys are ignored.
- Historical private builds contained a shared key; that historical key must be revoked/rotated externally.
- Generated DLLs and release ZIPs are not tracked in source.

## Release gates

GitHub Actions and `pack.ps1` enforce:

- manifest / csproj / `Plugin.ModVersion` equality,
- warnings-as-errors compilation,
- source scan for Groq- and OpenAI-key-shaped secrets,
- compiled DLL scan for Groq- and OpenAI-key-shaped secrets,
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
