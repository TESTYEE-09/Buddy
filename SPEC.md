# LethalAICrewmate — Design Spec

BepInEx 5 plugin for **Lethal Company v81**. Adds an AI-driven crewmate NPC ("Buddy",
name configurable) that helps the crew and chats via an LLM on OpenRouter (free models).

## Core architecture

- Single assembly `LethalAICrewmate.dll`, BepInEx 5.4.x plugin, netstandard2.1,
  Harmony patches only (no asset bundles, no custom network prefabs).
- **Host-authoritative**: all AI logic, spawning, and LLM calls run on the host only.
  Clients receive chat lines via Unity Netcode `CustomMessagingManager` named messages
  (works without prefab registration).
- Body: spawn a **MaskedPlayerEnemy** via `RoundManager.Instance.SpawnEnemyGameObject`
  (already a registered NetworkObject → transform sync is free). Immediately neutralize it:
  - disable its hostile AI behaviours (patch `MaskedPlayerEnemy.Update`/`DoAIInterval`
    and the attack/kill methods to no-op when the instance is flagged as a crewmate),
  - hide the mask (`maskTypes` / mask GameObjects disabled), call `SetSuit` so it looks
    like a normal crewmate,
  - keep the NavMeshAgent for movement.
  - Track crewmate instances in a static registry keyed by NetworkObjectId so patches
    can early-out (`if (CrewmateRegistry.IsCrewmate(__instance)) ...`).
  - Other enemies must not be distracted into killing it constantly; acceptable v1: leave as-is.

## Behaviour (host-side state machine, ticked from the enemy's DoAIInterval patch)

States: `FollowOwner` (default — follow nearest living player at ~3m),
`Stay`, `ReturnToShip`, `FetchScrap`.

- Commands via in-game text chat (case-insensitive, message starts with the crewmate
  name or "buddy"): "follow", "stay", "go to ship", "fetch/collect scrap". Commands are
  also passed to the LLM so it can acknowledge in character.
- **FetchScrap**: find nearest unheld `GrabbableObject` with `itemProperties.isScrap`,
  path to it; when within 2m, "pick it up" (host: set `heldByEnemy`-style hidden state —
  simplest robust approach: disable the item's mesh + colliders via
  `item.EnablePhysics(false)`, parent visual position to the crewmate each frame
  host-side and broadcast pickup/drop via the custom message channel so clients mirror
  the visual attach), walk to the ship, then drop: place at ship position via existing
  game flow (`item.transform.position`, `item.targetFloorPosition`, mark
  `isInShipRoom/isInElevator`, call `RoundManager.Instance.CollectNewScrapForPlayer`-style
  accounting only if a safe public path exists — otherwise just physically deliver it).
  Keep it simple and crash-proof: any failure → drop item in place and return to Follow.
- Periodic "observations" (every ~45–90 s, configurable, off by default at 0): the host
  builds a short game-state summary (planet, time of day, nearby enemies within 20m,
  nearby scrap count, ship scrap total) and asks the LLM for a one-liner remark.

## Chat / LLM integration

- Patch the server-side chat entry point (`HUDManager.AddPlayerChatMessageServerRpc`
  or `AddTextToChatOnServer`) on the **host** to observe player messages.
- A message triggers an LLM reply if it mentions the crewmate's name OR the player is
  within `ChatTriggerRange` (default 25 units) of the crewmate and the message ends in "?".
- LLM call: plain HTTPS POST `https://openrouter.ai/api/v1/chat/completions`,
  `Authorization: Bearer <key>`, JSON body {model, messages, max_tokens:150}. Use
  `UnityWebRequest` on a coroutine (run on a persistent plugin MonoBehaviour host object) —
  **never block the main thread**. Maintain a rolling history of the last ~12 exchanges.
  System prompt: configurable personality; instruct short (<25 words) in-character replies,
  and to output `[FOLLOW]/[STAY]/[SHIP]/[FETCH]` tags when the player asked for an action
  (parse tags out of the reply, execute the command, strip from displayed text).
- JSON: hand-rolled minimal serializer/parser or Unity JsonUtility with wrapper classes
  (no Newtonsoft dependency).
- **Proximity chat display**: host broadcasts the reply text + crewmate position via a
  named CustomMessagingManager message to all clients; each client shows it with
  `HUDManager.Instance.AddChatMessage(text, crewmateName)` **only if its local player is
  within `ChatHearRange`** (default 25 units, 0 = everyone). Dead players always hear it.
- Rate limiting: min 5 s between LLM calls, one in flight at a time, queue length 3 max.

## Config (BepInEx config file)

- `OpenRouter.ApiKey` (string, empty default — mod stays silent-but-functional NPC without it)
- `OpenRouter.Model` (default: `openai/gpt-oss-20b:free`)
- `Crewmate.Name` (default "Buddy"), `Crewmate.Personality` (system-prompt fragment)
- `Crewmate.Enabled`, `Crewmate.ChatHearRange`, `Crewmate.ChatTriggerRange`,
  `Crewmate.ObservationIntervalSeconds` (0=off)
- Spawn: one crewmate, spawned when the ship lands (`StartOfRound.OnShipLandedMiscEvents`
  or equivalent post-landing hook), despawned on ship leave. Host only spawns.

## Robustness rules

- Every Harmony patch body wrapped so an exception can never break vanilla flow
  (try/catch + log).
- All reflection/API touches of MaskedPlayerEnemy internals via publicized game libs
  (GameLibs nupkg) — no runtime reflection strings where a compile-time member exists.
- Mod must be safe when installed only on host (clients just won't see chat/visual attach;
  body still syncs). Ideally also no-op cleanly when a non-host has it installed.

## Deliverables

- `src/` C# project (csproj provided) compiling with zero errors/warnings-as-errors off.
- Thunderstore package: manifest.json, README.md, icon.png (256x256), CHANGELOG.md.
