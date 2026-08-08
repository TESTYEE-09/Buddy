# HANDOFF — LethalAICrewmate v2.4.1

## Current status

LethalAICrewmate is a BepInEx 5 mod for **Lethal Company v81** adding a friendly AI crewmate named Buddy.

Release target: **v2.4.1** (wire protocol **7**).

v2.4.1 hardening notes: terminal side effects require addressing Buddy; the enemy-kill guard cannot abort vanilla kills; config migration preserves Groq provider models; `[Vision] Enabled` is honored (default off); speech queue clears on lobby change; recovery teleports refuse void targets; spawn fallback never adopts engaged Masked enemies; ChatHearRange positional audio works; WAV validation parses RIFF chunks; 56 release checks.

Automated release requirements:

- compile against `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`,
- warnings treated as errors,
- source + compiled DLL secret scans (Groq- and OpenAI-shaped keys),
- manifest/csproj/plugin version equality,
- exact Thunderstore package whitelist,
- ZIP extraction validation,
- SHA-256 checksum,
- ready-to-install CI artifact + GitHub release on `main`.

## Architecture

- Buddy body: neutralized networked `MaskedPlayerEnemy`.
- Host authoritative for AI movement, item actions and all provider API calls.
- Multiplayer clients never receive the host API key.
- Every player must run the same LethalAICrewmate version/protocol.
- Buddy spawn is compatibility-gated. An unmodded/mismatched client disables Buddy rather than allowing a hostile/desynced Masked on that client.
- Late joins recover Buddy + held-item state.
- Buddy text and generated TTS audio are replicated to compatible clients.
- Native OpenAI Realtime voice (hold-to-talk, Ash voice) runs over an authenticated host WebSocket with a deterministic `execute_game_command` tool.
- Every Buddy reply is journaled with its paired player input to `BepInEx/LethalAICrewmate-responses.log` (host only; `[Logging] SaveResponses` toggle).

## AI providers

The host selects the provider (`OpenAI` default, `Groq` optional) and configures the key from the main menu with **Save / Test / Clear**.

OpenAI stock defaults:

- Chat: `gpt-5.6-luna` (Responses API, fast tier, low reasoning, low verbosity)
- STT: `gpt-live-transcribe`
- TTS: `gpt-4o-mini-tts`, voice `ash`
- Realtime voice: `gpt-realtime-2.1-mini`
- Vision: disabled by default

Groq defaults (legacy path):

- Chat: `openai/gpt-oss-120b`
- STT: `whisper-large-v3-turbo`
- TTS: `canopylabs/orpheus-v1-english`

Persistent keys: `LETHAL_AI_OPENAI_API_KEY` / `LETHAL_AI_GROQ_API_KEY` environment variables, or the main-menu Save button (Windows Credential Manager). Only the host performs provider requests.

## Commands

- `buddy follow`, `buddy stay`, `buddy go to ship`, `buddy fetch scrap`
- `buddy go forward` / `buddy scout ahead <n> metres` (bounded 4-18 m scouting)
- `buddy buy <qty> <item>`, `buddy open door <code>`, `buddy disable turret <code>`, `buddy open ship doors`, `buddy turn ship lights off`, `buddy status`
- `please spawn <item>` (bounded polite item spawner; hard per-round cap)

Explicit player commands are handled deterministically on the host. LLM-produced terminal tags are stripped and cannot spend credits or route the ship. On voice, the Realtime model must call `execute_game_command` for any catalogue command (the prompt makes ambiguous phrases default to the tool); on text chat the host executes before the LLM turn, so Buddy may only acknowledge confirmed results.

## Build

```powershell
powershell -File pack.ps1
```

or:

```powershell
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
```

Generated release binaries are intentionally ignored by Git. Use CI artifacts/releases for distribution.

## Multiplayer test checklist

When doing a real in-game multi-PC test:

1. Install the exact same release on host + clients.
2. Host enters a lobby; clients join and handshake.
3. Land on a moon; one Buddy spawns, never one per client.
4. Verify no hostile Masked kill behaviour from Buddy on any peer.
5. Verify follow/stay/ship/fetch commands from different players.
6. Verify two players can send the same text close together without one being deduped.
7. Verify late join after Buddy spawn restores Buddy identity/name/item visual.
8. Verify Buddy text appears once per compatible client and respects range.
9. Verify host TTS is heard by clients without client API keys.
10. Verify held scrap pickup/drop is mirrored and remains grabbable after drop.
11. Join with an unmodded/mismatched client: Buddy must remain disabled/despawn rather than becoming hostile.
12. Return to a fully compatible lobby: Buddy should become spawnable again on the landed round polling path.
13. Try an explicit `buddy buy ...` command and verify only one purchase occurs.
14. Clear/break the host API key: Buddy movement remains functional and API features fail without crashing the game.
15. Verify native Realtime push-to-talk: host and remote hold-to-talk produce one synced Buddy voice reply, and commands spoken into it execute once.

## Security note

Old private builds/history included a shared Groq key. Deleting old tracked binaries from the current branch does **not** revoke a credential or erase Git history. Revoke/rotate that old key in Groq before any public distribution.

## Release flow

1. Make changes on a feature branch (`ship/**` also builds) and bump manifest/csproj/plugin versions together.
2. Require a green feature-branch and pull-request GitHub Actions build.
3. Merge to `main`.
4. The `main` workflow reruns all validation and packaging gates.
5. Only after those gates pass, CI publishes `vX.Y.Z` from that exact `main` SHA with the tested ZIP + `SHA256SUMS.txt`.
6. If that version already has a GitHub Release, CI leaves it untouched instead of overwriting published assets.
