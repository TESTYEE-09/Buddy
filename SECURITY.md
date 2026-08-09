# Security

## API keys

Never commit a provider API key, put one in a release ZIP, log it, or send it through multiplayer messages.

For the strongest practical setup, set the host machine environment variable
`LETHAL_AI_OPENAI_API_KEY` (OpenAI provider) or `LETHAL_AI_GROQ_API_KEY` (Groq provider)
before starting the game. The mod reads it in memory and never writes that value to its
config or logs.

Keys entered in the main-menu panel are stored in Windows Credential Manager, never in the config
file. The old `[Security] PersistApiKey` setting and its plaintext config storage were removed.

Any key found in a pre-3.0.1 config is imported into Credential Manager on first load and the
plaintext entry is deleted, including when secure storage fails — in that case the key stays usable
for the session only and a warning is logged. Multiplayer clients do not need and do not receive the
host key.

Any credential that has ever been committed or shared must be considered exposed and revoked/rotated at the provider. Removing it from the current source tree is not credential revocation.

## Multiplayer trust boundary

- The host is authoritative for Buddy spawning, AI, item actions and all provider API calls.
- `AllowRemoteVoice` permits matching clients to use the relay, but `RemoteVoiceInPublicLobbies = false`
  accepts remote audio only after Steam visibility is positively identified as friends/invite-only.
  Public, missing, unknown and failed visibility checks are blocked. Set it to `true` only if the host
  explicitly accepts remote audio and provider cost in untrusted/unknown lobbies.
- Remote purchases, routes, polite item spawning and ship/facility changes follow the same fail-closed
  boundary through `RemoteGameActionsInPublicLobbies = false`. The host and read-only status/store/moon
  queries remain available. Arbitrary terminal sentence passthrough is not exposed.
- Player chat cannot spend the host's AI-provider budget in public, missing or unknown-visibility lobbies unless the host explicitly enables `RemoteAiInPublicLobbies`.
- When enabled, remote voice is transport-sender-bound, exact-version-gated, Buddy-range-gated before allocation,
  rate-limited, size-limited, transfer-capped and WAV/RMS-validated before it reaches the provider.
- Clients send only a compatibility hello and, when explicitly enabled, bounded voice transfers through the mod's custom networking path.
- Clients accept Buddy state only from `NetworkManager.ServerClientId` after an exact version/protocol compatibility handshake. The handshake is compatibility evidence, not authentication against a malicious host; lobby clients trust their host by design.
- Buddy does not spawn if any connected remote player is unmodded or incompatible.

## Local response journal

`[Logging] SaveResponses = false` is the default and older configs are migrated to off. Opting in stores
raw player chat, voice transcripts, Buddy replies and confirmed tool results on the host at
`BepInEx/LethalAICrewmate-responses.log` (bounded to 8 MB). When response saving is off, startup removes
an existing journal left by an earlier version or opt-in session. Treat this file as sensitive player data and
obtain the crew's informed consent before enabling or sharing it. Input/reply correlation uses explicit
turn IDs so concurrent chat, deterministic commands and Realtime tool calls cannot cross-pair records.

Buddy's spoken output is AI-generated. Model output has no game-action tool: purchases, routing, movement and ship/facility changes originate only from deterministic parsing of player input. OpenAI Realtime sessions are isolated per turn so one speaker's conversation cannot carry instructions into another speaker's turn.
- Ordinary logs contain lengths and status codes, not raw chat, player names, voice transcripts or provider error bodies.
- Host screenshots are disabled in the hardened public build. The retained Vision config key is inert for compatibility.
- The optional slow-burn character arc persists only numeric progress and its quota-cycle baseline in the current game save.
  It stores no dialogue, transcript, player name or personal fact. Arc stages affect dialogue/voice presentation
  only and cannot grant movement, terminal, spawn, combat or network authority.
- LLM text cannot directly buy items, route moons or change Buddy movement state; those actions require deterministic player-command parsing.

## Player relationships and social tracking

`[Character] PlayerRelationships` lets Buddy treat individual crewmates differently. What it stores is
deliberately minimal:

- at most eight entries per save, each three small bounded integers (trust, familiarity, friction),
- keyed by a 16-bit non-reversible digest of the lowercased player name,
- no names, Steam IDs, chat text, transcripts or timestamps are written to disk,
- nothing is replicated to clients or sent anywhere except the host's own save file.

The player's in-game display name — which every crewmate can already see — is included in the prompt
sent to the configured AI provider, exactly as chat messages already are. It is truncated before it
reaches the prompt. Relationship state affects tone, who Buddy answers first and who he re-acquires
when following; it grants no authority and cannot change what a command is allowed to do.

`[Crewmate] SocialAwareness` tracks at most four recent speakers in memory only. Speaker identity is
resolved from the host's own player list. Note that vanilla Lethal Company chat is unauthenticated,
so a modded client can already make a chat line appear to come from another player; with social
awareness on, that can mislead Buddy about *who* to answer or walk toward. It cannot grant command
authority — restricted actions still go through the fail-closed remote-action boundary above.

## Final story stage

`[Character] FinalStageHostileSpawns` is **off by default**. When a host turns it on, and only at the
final story stage with the slow burn enabled, Buddy may occasionally release one of the current
moon's own creatures near a working crewmate. Enable it only with the crew's agreement.

The gate is host-only and cannot be reached from outside the host's own director:

- no chat command, terminal command, model tool call or network message can request a hunt,
- capped at two per round, with a seven-minute minimum interval and a delay after landing,
- never targets a player standing in the ship,
- only uses entities already present in the current moon's own spawn table,
- never spawns another Masked, which would collide with Buddy's identification handshake.

## Voice device sharing

Buddy's push-to-talk shares Unity's global microphone with the game's own voice chat. Buddy restores
the game's capture when it releases the device so the crew keep hearing each other, and it never
changes a player's own mute state.

## Release protections

CI rejects:

- mismatched manifest/project/plugin versions,
- tracked generated release DLL/ZIP files,
- Groq-key- and OpenAI-key-shaped secrets in source,
- Groq-key- and OpenAI-key-shaped secrets in compiled DLL bytes (ASCII and UTF-16),
- compiler warnings or errors,
- invalid Thunderstore package structure,
- an invalid Thunderstore icon size.

The release workflow scans every commit in the release branch. Older unrelated tags/branches may still contain a historical provider credential; that credential must remain revoked, and deleting or rewriting a public Git ref is not a substitute for rotation.

Release ZIPs include a SHA-256 checksum.

## Reporting

For a suspected vulnerability, avoid posting credentials or sensitive logs in a public issue. Revoke exposed provider credentials immediately, then provide a minimal reproduction without secrets.
