# HANDOFF — LethalAICrewmate v1.4.0

## Current status

LethalAICrewmate is a BepInEx 5 mod for **Lethal Company v81** adding a friendly AI crewmate named Buddy.

Release target: **v1.4.0**.

Automated release requirements:

- compile against `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`,
- warnings treated as errors,
- source + compiled DLL Groq-key secret scans,
- manifest/csproj/plugin version equality,
- exact Thunderstore package whitelist,
- ZIP extraction validation,
- SHA-256 checksum,
- ready-to-install CI artifact.

## Architecture

- Buddy body: neutralized networked `MaskedPlayerEnemy`.
- Host authoritative for AI movement, item actions and all Groq calls.
- Multiplayer clients never receive the Groq key.
- Every player must run the same LethalAICrewmate version/protocol.
- Buddy spawn is compatibility-gated. An unmodded/mismatched client disables Buddy rather than allowing a hostile/desynced Masked on that client.
- Late joins recover Buddy + held-item state.
- Buddy text and generated TTS audio are replicated to compatible clients.

## Groq

The host configures the key from the main menu with **Save / Test / Clear**.

Defaults:

- Chat: `llama-3.3-70b-versatile`
- STT: `whisper-large-v3-turbo`
- TTS: `canopylabs/orpheus-v1-english`
- Vision: off by default

Only the host performs Groq requests. The main-menu Test button validates the key before play.

## Commands

- `buddy follow`
- `buddy stay`
- `buddy go to ship`
- `buddy fetch scrap`

Explicit player terminal commands such as route/buy are handled deterministically. LLM-produced terminal tags are stripped and cannot spend credits or route the ship.

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
9. Verify host TTS is heard by clients without client Groq keys.
10. Verify held scrap pickup/drop is mirrored and remains grabbable after drop.
11. Join with an unmodded/mismatched client: Buddy must remain disabled/despawn rather than becoming hostile.
12. Return to a fully compatible lobby: Buddy should become spawnable again on the landed round polling path.
13. Try an explicit `buddy buy ...` command and verify only one purchase occurs.
14. Clear/break the host Groq key: Buddy movement remains functional and API features fail without crashing the game.

## Security note

Old private builds/history included a shared Groq key. Deleting old tracked binaries from the current branch does **not** revoke a credential or erase Git history. Revoke/rotate that old key in Groq before any public distribution.

## Release flow

1. Make changes on a feature branch.
2. Require a green GitHub Actions build.
3. Merge to `main`.
4. Require the `main` build to pass.
5. Create tag `v1.4.0` at the tested main commit.
6. Tag CI publishes the exact tested ZIP + `SHA256SUMS.txt` as a GitHub Release.
