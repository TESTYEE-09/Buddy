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
- Remote push-to-talk is enabled for matching friends by default. Set `[Security] AllowRemoteVoice = false`
  when hosting an untrusted public lobby.
- In public Steam lobbies remote push-to-talk is additionally rejected by default
  (`[Security] RemoteVoiceInPublicLobbies = false`): strangers cannot upload audio that would
  consume the host's provider budget or be transcribed by the speech service. Set it to `true`
  to allow remote voice everywhere. Friends/invite-only lobbies always allow remote voice.
- When enabled, remote voice is sender-bound, compatibility-gated, range-gated before allocation,
  rate-limited, size-limited, transfer-capped and WAV/RMS-validated before it reaches the provider.
- Clients send only a compatibility hello and, when explicitly enabled, bounded voice transfers through the mod's custom networking path.
- Clients accept Buddy state only from `NetworkManager.ServerClientId` after a successful exact version/protocol handshake.
- Buddy does not spawn if any connected remote player is unmodded or incompatible.
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
