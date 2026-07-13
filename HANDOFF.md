# HANDOFF — LethalAICrewmate

Shippable **v1.0.1** on Windows (2026-07-13). Built, packaged, installed into r2modman profile **THE MOD PACK**.

## Status

| Item | State |
|------|--------|
| Compile (v81 GameLibs) | OK — 0 warnings/errors |
| Thunderstore zip | `LethalAICrewmate-1.0.1.zip` |
| r2modman install | `…\profiles\THE MOD PACK\BepInEx\plugins\TESTYEE-LethalAICrewmate\` |
| In-game test | **Not run** (needs you in a lobby) |

## What 1.0.1 fixed for ship

- Client-side crewmate net-id sync so hostility patches apply for clients
- Spawn retries + NavMesh snap after land
- Extra Masked guards (LateUpdate, DetectNoise, HitEnemy)
- Scrap double-count guard; LLM session reset on leave; host item-attach loopback skip

## What this is

BepInEx 5 mod for **Lethal Company v81** adding an AI crewmate ("Buddy"):

- Host-only spawns a **MaskedPlayerEnemy** when the ship lands, neutralized + suited, despawn on leave.
- States: FollowOwner / Stay / ReturnToShip / FetchScrap.
- Chat commands: `buddy follow / stay / go to ship / fetch scrap`.
- Optional LLM via OpenRouter; proximity chat net message.
- Clients: install recommended for chat, attach visuals, and kill suppression.

## Build

```powershell
dotnet build src/LethalAICrewmate.csproj -c Release
# or full package:
powershell -File pack.ps1
```

## In-game smoke test (when back)

1. Launch **THE MOD PACK** via r2modman (not bare Steam — BepInEx lives in the profile).
2. Host → land on moon → `LogOutput.log`: `Spawning crewmate` → `spawned successfully`.
3. Commands: follow / stay / ship / fetch scrap.
4. Optional: set `OpenRouter.ApiKey` in `BepInEx/config/com.lethalaicrewmate.buddy.cfg`.
5. Second client with mod: proximity chat + no double lines; Buddy should not client-side kill.

## Fallback if Masked is unworkable

Player-slot approach like [Lethal-Bots](https://github.com/T-Rizzle12/Lethal-Bots) (v81).

## Conventions

- Every Harmony patch body try/caught.
- Host-authoritative: `CrewmateSpawner.IsHost()`.
- No Newtonsoft; hand-rolled JSON in `LlmClient`.
- Crewmates tracked in `CrewmateRegistry` (+ `KnownCrewmateNetIds` for clients).
