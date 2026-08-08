# Security

## API keys

Never commit a Groq API key, put one in a release ZIP, log it, or send it through multiplayer messages.

LethalAICrewmate stores the host key locally in:

`BepInEx/config/com.lethalaicrewmate.buddy.cfg`

The main-menu password field writes to that local BepInEx config. Multiplayer clients do not need and do not receive the host key.

Historical private builds contained a shared Groq credential. Any credential that has ever been committed or shared must be considered exposed and revoked/rotated at the provider. Removing it from the current source tree is not credential revocation.

## Multiplayer trust boundary

- The host is authoritative for Buddy spawning, AI, item actions and Groq calls.
- Clients send only a compatibility hello through the mod's custom networking path.
- Clients accept Buddy state only from `NetworkManager.ServerClientId` after a successful exact version/protocol handshake.
- Buddy does not spawn if any connected remote player is unmodded or incompatible.
- LLM text cannot directly buy items, route moons or change Buddy movement state; those actions require deterministic player-command parsing.

## Release protections

CI rejects:

- mismatched manifest/project/plugin versions,
- tracked generated release DLL/ZIP files,
- Groq-key-shaped secrets in source,
- Groq-key-shaped secrets in compiled DLL bytes (ASCII and UTF-16),
- compiler warnings or errors,
- invalid Thunderstore package structure,
- an invalid Thunderstore icon size.

Release ZIPs include a SHA-256 checksum.

## Reporting

For a suspected vulnerability, avoid posting credentials or sensitive logs in a public issue. Revoke exposed provider credentials immediately, then provide a minimal reproduction without secrets.
