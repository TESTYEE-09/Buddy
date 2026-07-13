# HANDOFF — LethalAICrewmate (for Claude Code on the Windows PC)

You are picking up a finished-but-untested Lethal Company mod. It was built and
compile-verified on a Mac on 2026-07-13; your job is in-game testing and iteration.

## What this is

BepInEx 5 mod for **Lethal Company v81** adding an AI crewmate ("Buddy"):

- Host-only spawns a **MaskedPlayerEnemy** when the ship lands (`StartOfRound.OnShipLandedMiscEvents`
  postfix), neutralizes it (hostile AI no-oped via Harmony guards keyed on a registry,
  mask objects hidden, `SetSuit` applied), despawns on `StartOfRound.ShipLeave`.
- Host-side state machine (`CrewmateAI`): FollowOwner / Stay / ReturnToShip / FetchScrap.
  Fetch = walk to nearest scrap, parent it to the body (visual attach mirrored to clients
  via custom net message), deliver to ship, `CollectNewScrapForThisRound`.
- Chat commands (in-game text chat, host observes via `HUDManager.AddTextToChatOnServer` /
  `AddPlayerChatMessageServerRpc` postfixes): "buddy follow / stay / go to ship / fetch scrap".
- **LLM chat**: host POSTs to `https://openrouter.ai/api/v1/chat/completions`
  (default model `openai/gpt-oss-20b:free`, config `OpenRouter.ApiKey`), UnityWebRequest
  coroutine, hand-rolled JSON, 12-turn history, 5s rate limit, queue cap 3. The LLM can
  emit `[FOLLOW]/[STAY]/[SHIP]/[FETCH]` tags which are parsed, executed, stripped.
- **Proximity chat**: reply broadcast via `CustomMessagingManager` named message
  `LethalAICrewmate_Chat`; each client displays with `HUDManager.AddChatMessage` only if
  its local player is within `ChatHearRange` (default 25u, 0=all; dead players always hear).
  NOTE: the host displays locally in `LlmClient.HandleAssistantReply` and the receive
  handler early-outs on `IsServer` — this prevents double display; keep that invariant.

## Files

- `src/*.cs` — 9 source files: Plugin, CrewmateRegistry, CrewmateSpawner,
  MaskedNeutralizePatches, CrewmateAI, ChatPatches, LlmClient, NetMessenger, ProximityChat.
- `src/LethalAICrewmate.csproj` — netstandard2.1; NuGet: `BepInEx.Core 5.4.21`,
  `BepInEx.PluginInfoProps 2.1.0`, `LethalCompany.GameLibs.Steam 81.0.5-ngd.0` (real v81
  publicized game assemblies, on nuget.org), `UnityEngine.Modules 2022.3.62`.
  Extra NuGet feed https://nuget.bepinex.dev/v3/index.json is declared in the csproj.
- `ThunderstorePackage/` + `LethalAICrewmate-1.0.0.zip` — manifest, icon, README,
  CHANGELOG, dll (dependency string `BepInEx-BepInExPack-5.4.2100`).
- `SPEC.md` — original design spec (source of truth for intended behavior).

## Build (any OS)

    dotnet build src/LethalAICrewmate.csproj -c Release
    # output: src/bin/Release/netstandard2.1/LethalAICrewmate.dll

Needs .NET SDK 8+. Restore pulls everything from NuGet; no game files needed to compile.

## Test plan (nothing below has been verified in-game yet)

1. Install BepInExPack + drop the dll in `BepInEx/plugins`. Set `OpenRouter.ApiKey` in
   `BepInEx/config/com.lethalaicrewmate.buddy.cfg` (free key from openrouter.ai/keys).
2. Host a game, land on a moon. Check `BepInEx/LogOutput.log` for
   "Spawning crewmate" → "spawned successfully". Riskiest area: `FindMaskedEnemyType()`
   (tries current level lists → all levels → QuickMenuManager.testAllEnemiesLevel →
   Resources scan) and whether the neutralize patches fully stop Masked hostility
   (check `MaskedNeutralizePatches.cs` covers all attack paths in v81's MaskedPlayerEnemy).
3. Verify: follows you; "buddy stay/follow/fetch scrap/go to ship" commands; LLM replies
   appear in chat with the Buddy name; a second (client) player only sees replies when
   within ~25u and never sees doubles; fetch actually delivers scrap and it counts.
4. Known soft spots to watch: NavMesh warping when spawned near ship edge; scrap value
   accounting (`CollectNewScrapForThisRound`) may double-count if a player later grabs the
   same item; other enemies will still target Buddy (accepted v1); animations may look
   idle-ish since vanilla Masked behaviours are suppressed (cosmetic).
5. If MaskedPlayerEnemy proves unworkable, the fallback plan is the player-slot approach
   used by LethalBots (github.com/T-Rizzle12/Lethal-Bots, explicitly v81) — real
   PlayerControllerB body bound to a custom AI brain. Read their spawner before rewriting.

## Conventions in this codebase

- Every Harmony patch body is try/caught so vanilla flow can never break — keep that.
- Host-authoritative everywhere: guard with `CrewmateSpawner.IsHost()`.
- No Newtonsoft; JSON is hand-rolled in `LlmClient` (Escape/ParseAssistantContent).
- Crewmate instances tracked in `CrewmateRegistry` by NetworkObjectId; all patches
  early-out for non-crewmate Masked enemies so vanilla Masked spawns stay untouched.
