# Security

Buddy's release security goal is practical: a downloaded mod must not contain or expose somebody else's API key, and model/player input must not become general access to the host PC.

## API keys

Only the host needs an OpenAI key. Use either:

- the `LETHAL_AI_OPENAI_API_KEY` environment variable, held in memory; or
- Buddy's native settings page, which stores the key in Windows Credential Manager.

The key is never written to the BepInEx config, response journal, ordinary logs or multiplayer messages. Older plaintext OpenAI and Groq config entries are removed during migration. If a credential was ever committed or shared, revoke it at the provider; deleting it from Git history is not credential rotation.

Clients never receive the host's key. The host alone opens the authenticated OpenAI Realtime connection.

## Model tool boundary

Buddy uses `gpt-realtime-2.1-mini` function calling. The model can request only the tools explicitly registered by the mod:

- follow, stay, return, bounded scouting and scrap fetching;
- current ship status, moons and store information;
- routing and bounded purchases;
- supported coded facility objects, hangar doors and ship lights;
- a deliberately bounded validated-item spawn request.

Each request is parsed into typed arguments, validated and executed on the host's Unity thread. The real result is returned to the model before it replies. Quantities, scout distance, item prefabs, credits, dropship capacity, facility codes and per-round spawn counts remain host-clamped.

The model is not given tools for filesystem access, shell commands, process execution, arbitrary URLs, arbitrary terminal text, credentials or multiplayer networking. Player text, names, audio and sensor strings cannot add a new tool or replace the system instructions. This is the main PC-safety boundary.

## Multiplayer boundary

- The host is authoritative for Buddy spawning, AI requests, movement, game tools and provider calls.
- Clients accept Buddy state only from `NetworkManager.ServerClientId` after the exact version/protocol compatibility handshake.
- Buddy does not spawn if a connected remote player is unmodded, incompatible or still inside the handshake grace period.
- `AllowRemoteVoice` is the host switch for compatible clients to relay push-to-talk audio. It does not depend on unreliable Steam lobby-visibility detection.
- Remote voice is transport-sender-bound, exact-version-gated, Buddy-range-gated before allocation, rate-limited, size-limited, transfer-capped and WAV/RMS-validated.
- Vanilla typed-chat identity is not strong authentication. Run Buddy with players you trust not to deliberately spend host credits or API budget. Even malicious phrasing remains confined to the bounded in-game tool surface above.
- The final-stage hostile-spawn director is not a model tool and cannot be requested by chat, voice or network messages.

## Response journal

`[Logging] SaveResponses = false` is the default. With it off, Buddy removes an existing journal during startup and does not persist conversation.

Opting in stores typed player input, Buddy replies, observations and confirmed tool results at `BepInEx/LethalAICrewmate-responses.log`, bounded to 8 MB. Voice audio goes directly to Realtime and is not separately transcribed into the journal. `SavePromptContext` is a second opt-in for the system prompt and live sensor context. Treat the journal as sensitive player data and obtain the whole crew's consent before enabling or sharing it.

Ordinary logs contain status and lengths, not raw player speech, chat, names, API response bodies or credentials. Host screenshots are disabled in the public build.

The live Realtime connection and compact conversation memory are in-memory only and reset with the gameplay/session lifecycle. The character arc separately persists bounded numeric progress, not chat, transcripts, Steam IDs or personal facts.

## Relationships and final story stage

Optional relationship state stores at most eight entries of three small bounded integers, keyed by a 16-bit non-reversible digest of the lowercased display name. It stores no name, Steam ID, dialogue, transcript or timestamp and is not replicated to clients.

`FinalStageHostileSpawns` is enabled for new installs and should be disabled unless the crew agrees. It is host-director-only, capped at two per round, separated by seven minutes and a post-landing delay, excludes players inside the ship, uses the current moon's spawn table and never spawns another Masked.

## Release protections

CI rejects:

- mismatched manifest, project and plugin versions;
- tracked generated release DLL/ZIP files;
- OpenAI- or legacy Groq-key-shaped secrets in source and compiled DLL bytes, in ASCII and UTF-16;
- reintroduction of retired providers, separate transcription models or deterministic command parsers;
- missing Realtime function-call/result handling;
- compiler warnings, failed regression checks, invalid package contents or an invalid icon.

Release ZIPs include a SHA-256 checksum. GitHub secret scanning and push protection should remain enabled on the public repository as an additional server-side guard.

## Reporting

Do not post credentials or sensitive journals in a public issue. Revoke any exposed key immediately, then report a minimal reproduction without secrets.
