# LethalAICrewmate

Adds an AI-driven crewmate NPC ("Buddy" by default) to **Lethal Company v81**. The host spawns a neutralized Masked body that can follow players, stay put, return to the ship, fetch scrap, and optionally chat via OpenRouter LLMs.

## Requirements

- **BepInEx 5.4.x** (BepInExPack)
- Lethal Company **v81** (built against v81 publicized game libs)
- **The host must install this mod** for the crewmate to spawn and for AI/chat to work. Clients should also install it for proximity chat, scrap-attach visuals, and correct client-side hostility suppression.

## Install

1. Install [BepInExPack](https://thunderstore.io/c/lethal-company/p/BepInEx/BepInExPack/) for Lethal Company.
2. Install this mod via Thunderstore / r2modman / Gale, **or** copy `LethalAICrewmate.dll` into `BepInEx/plugins/`.
3. Launch the game once so the config file is generated under `BepInEx/config/com.lethalaicrewmate.buddy.cfg`.

## Quick start

1. Host a lobby (mod is host-authoritative).
2. Land on a moon — Buddy spawns outside the ship (check `BepInEx/LogOutput.log` for `spawned successfully`).
3. Chat commands (no API key needed):

| Command | Effect |
|---------|--------|
| `buddy follow` | Follow nearest / owner player (~3 m) |
| `buddy stay` | Hold position |
| `buddy go to ship` / `buddy ship` | Path back to the ship |
| `buddy fetch` / `buddy collect scrap` | Pick up nearest scrap and deliver to ship |

4. Optional chat: set `OpenRouter.ApiKey` (free key from [openrouter.ai/keys](https://openrouter.ai/keys)).

## Config (`BepInEx/config/com.lethalaicrewmate.buddy.cfg`)

| Section | Key | Default | Notes |
|--------|-----|---------|--------|
| OpenRouter | ApiKey | *(empty)* | Leave empty for a silent but fully functional NPC |
| OpenRouter | Model | `openai/gpt-oss-20b:free` | Any OpenRouter model id |
| Crewmate | Name | Buddy | Chat name and command prefix |
| Crewmate | Personality | *(short prompt)* | System-prompt flavor |
| Crewmate | Enabled | true | Master spawn toggle |
| Crewmate | ChatHearRange | 25 | Units; **0 = everyone hears** |
| Crewmate | ChatTriggerRange | 25 | Range for `?` questions to trigger chat |
| Crewmate | ObservationIntervalSeconds | 0 | Unsolicited remarks; **0 = off** |

Without a key, Buddy still spawns and obeys movement/scrap commands; only LLM chat is disabled.

Commands are also sent to the LLM (when configured) so Buddy can acknowledge in character. The model may emit tags `[FOLLOW]` `[STAY]` `[SHIP]` `[FETCH]` which are applied and stripped from displayed text.

Messages that **mention the crewmate name**, or that end with `?` while you are within `ChatTriggerRange`, can trigger a reply.

## Behaviour notes

- One crewmate per landing; spawns when the ship lands; despawned when the ship leaves.
- Host-authoritative: AI, spawn, and LLM calls run on the host only.
- Proximity chat: clients only show Buddy’s lines if within `ChatHearRange` (dead players always hear).
- Fetch scrap is best-effort and crash-safe; failures drop the item and return to follow.
- Other enemies may still target Buddy (accepted v1 limitation).
- Animations can look idle-ish because vanilla Masked attack behaviours are suppressed.

## Troubleshooting

- **No Buddy:** confirm you are the lobby **host**, `Crewmate.Enabled` is true, you are **landed** on a moon, and `LogOutput.log` shows `Spawning crewmate` / `spawned successfully`. If spawn fails, the mod retries a few times after landing.
- **Buddy attacks players:** ensure clients also install the mod (1.0.1+ syncs crewmate identity). Host-only is still required for AI.
- **No chat:** set a valid `OpenRouter.ApiKey`; check BepInEx log for HTTP errors / rate limits (5 s min between calls).
- **Clients can’t see chat/attach:** install the mod on clients too (body still network-syncs either way).

## Build from source

```bash
dotnet build src/LethalAICrewmate.csproj -c Release
# -> src/bin/Release/netstandard2.1/LethalAICrewmate.dll
```

Requires .NET SDK 8+. Restore uses nuget.org + nuget.bepinex.dev (no game install needed to compile).

## License / source

https://github.com/TESTYEE-09/LethalAICrewmate
