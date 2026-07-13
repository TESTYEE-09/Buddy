# LethalAICrewmate

Adds an AI-driven crewmate NPC ("Buddy" by default) to **Lethal Company**. The host spawns a neutralized Masked body that can follow players, stay put, return to the ship, fetch scrap, and optionally chat via OpenRouter LLMs.

## Requirements

- **BepInEx 5.4.x** (BepInExPack)
- Lethal Company (tested against v81 game libs)
- **The host must install this mod** for the crewmate to spawn and for AI/chat to work. Clients benefit from proximity chat and item-attach visuals if they also install it, but the body still network-syncs without client install.

## Install

1. Install [BepInExPack](https://thunderstore.io/c/lethal-company/p/BepInEx/BepInExPack/) for Lethal Company.
2. Install this mod via Thunderstore / r2modman / Gale, **or** copy `LethalAICrewmate.dll` into `BepInEx/plugins/`.
3. Launch the game once so the config file is generated.

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

### Free OpenRouter API key

1. Create an account at [https://openrouter.ai](https://openrouter.ai).
2. Open **Keys** and create an API key (free tier is enough for short replies).
3. Paste the key into `OpenRouter.ApiKey` in the config file.
4. Optionally set `OpenRouter.Model` to another free model from the OpenRouter model list.

Without a key, Buddy still spawns and obeys movement/scrap commands; only LLM chat is disabled.

## Commands (in-game text chat)

Case-insensitive. Address the crewmate by name (default **Buddy**) or the word **buddy**:

| Command | Effect |
|---------|--------|
| `buddy follow` | Follow nearest / owner player (~3 m) |
| `buddy stay` | Hold position |
| `buddy go to ship` / `buddy ship` | Path back to the ship |
| `buddy fetch` / `buddy collect scrap` | Pick up nearest scrap and deliver to ship |

Commands are also sent to the LLM (when configured) so Buddy can acknowledge in character. The model may emit tags `[FOLLOW]` `[STAY]` `[SHIP]` `[FETCH]` which are applied and stripped from displayed text.

Messages that **mention the crewmate name**, or that end with `?` while you are within `ChatTriggerRange`, can trigger a reply.

## Behaviour notes

- One crewmate per landing; spawns when the ship lands; despawned when the ship leaves.
- Host-authoritative: AI, spawn, and LLM calls run on the host only.
- Proximity chat: clients only show Buddy’s lines if within `ChatHearRange` (dead players always hear).
- Fetch scrap is best-effort and crash-safe; failures drop the item and return to follow.

## Troubleshooting

- **No Buddy:** confirm you are the lobby **host**, `Crewmate.Enabled` is true, and you are landed on a moon.
- **No chat:** set a valid `OpenRouter.ApiKey`; check BepInEx log for HTTP errors.
- **Clients can’t see chat/attach:** install the mod on clients too (body still syncs either way).
