# Security

## API keys

Never commit a provider API key, put one in a release ZIP, log it, or send it through multiplayer messages.

For the strongest practical setup, set the host machine environment variable
`LETHAL_AI_OPENAI_API_KEY` (OpenAI provider) or `LETHAL_AI_GROQ_API_KEY` (Groq provider)
before starting the game. The mod reads it in memory and never writes that value to its
config or logs.

Keys entered in the main-menu panel are session-only by default. Set `[Security] PersistApiKey = true`
only if you explicitly accept plaintext local storage in:

`BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Pre-existing keys in that config remain supported as a legacy fallback. Move them to the environment
variable and clear the config entry after confirming the game starts successfully. Multiplayer clients
do not need and do not receive the host key.

Historical private builds contained a shared Groq credential. Any credential that has ever been committed or shared must be considered exposed and revoked/rotated at the provider. Removing it from the current source tree is not credential revocation.

## Multiplayer trust boundary

- The host is authoritative for Buddy spawning, AI, item actions and all provider API calls.
- `AllowRemoteVoice` permits matching clients to use the relay, but `RemoteVoiceInPublicLobbies = false`
  accepts remote audio only after Steam visibility is positively identified as friends/invite-only.
  Public, missing, unknown and failed visibility checks are blocked. Set it to `true` only if the host
  explicitly accepts remote audio and provider cost in untrusted/unknown lobbies.
- Remote purchases, routes, polite item spawning and ship/facility changes follow the same fail-closed
  boundary through `RemoteGameActionsInPublicLobbies = false`. The host and read-only status/store/moon
  queries remain available. Arbitrary terminal sentence passthrough is not exposed.
- When enabled, remote voice is sender-bound, compatibility-gated, range-gated before allocation,
  rate-limited, size-limited, transfer-capped and WAV/RMS-validated before it reaches the provider.
- Clients send only a compatibility hello and, when explicitly enabled, bounded voice transfers through the mod's custom networking path.
- Clients accept Buddy state only from `NetworkManager.ServerClientId` after a successful exact version/protocol handshake.
- Buddy does not spawn if any connected remote player is unmodded or incompatible.

## Local response journal

`[Logging] SaveResponses = false` is the default and older configs are migrated to off. Opting in stores
raw player chat, voice transcripts, Buddy replies and confirmed tool results on the host at
`BepInEx/LethalAICrewmate-responses.log` (bounded to 2 MB). Treat this file as sensitive player data and
obtain the crew's informed consent before enabling or sharing it. Input/reply correlation uses explicit
turn IDs so concurrent chat, deterministic commands and Realtime tool calls cannot cross-pair records.

Buddy's spoken output is AI-generated.
- The optional slow-burn character arc persists only numeric progress and its quota-cycle baseline in the current game save.
  It stores no dialogue, transcript, player name or personal fact. Arc stages affect dialogue/voice presentation
  only and cannot grant movement, terminal, spawn, combat or network authority.
- LLM text cannot directly buy items, route moons or change Buddy movement state; those actions require deterministic player-command parsing.

## Release protections

CI rejects:

- mismatched manifest/project/plugin versions,
- tracked generated release DLL/ZIP files,
- Groq-key- and OpenAI-key-shaped secrets in source,
- Groq-key- and OpenAI-key-shaped secrets in compiled DLL bytes (ASCII and UTF-16),
- compiler warnings or errors,
- invalid Thunderstore package structure,
- an invalid Thunderstore icon size.

Release ZIPs include a SHA-256 checksum.

## Reporting

For a suspected vulnerability, avoid posting credentials or sensitive logs in a public issue. Revoke exposed provider credentials immediately, then provide a minimal reproduction without secrets.
