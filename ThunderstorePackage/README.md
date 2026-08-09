# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

Buddy is an AI crewmate for **Lethal Company v81**. He walks with you after landing, talks out loud, follows natural requests, buys from the store, operates supported ship and facility controls, fetches scrap and reacts to confirmed danger.

Across quotas, survived shifts and deaths he actually witnesses, the friendly coworker can slowly become less friendly. The story is optional.

## Setup

### 1. Install Buddy

Install the same Buddy version on every player. Thunderstore installs BepInExPack and LethalSettings automatically.

### 2. Add OpenAI API credit

Buddy uses the **OpenAI API**, which is billed separately from a ChatGPT subscription.

1. Open the [OpenAI Platform billing page](https://platform.openai.com/settings/organization/billing/overview).
2. Add a payment method and purchase API credit. **The current minimum purchase is $5 USD**; OpenAI may show a default amount of $10.
3. Turn off automatic recharge if you do not want automatic top-ups.

OpenAI API credits are usage credit, not a subscription. They can take a few minutes to appear, expire after one year, and are non-refundable under OpenAI's current prepaid-billing rules.

### 3. Create and save the key

1. Create an API key at [platform.openai.com/api-keys](https://platform.openai.com/api-keys).
2. Keep the key private. Never paste it into Discord, a modpack, a public issue or a multiplayer chat.
3. In Lethal Company, open **Settings > Mod Settings > Buddy**.
4. Paste the key into **API key**, press **Save key**, then press **Test key**.

The key is stored in Windows Credential Manager. Only the host needs a key; friends do not enter yours.

### 4. Start the lobby

Host a lobby with the same Buddy version installed for everyone. In orbit Buddy is a voice terminal with no body. His body spawns outside only after the ship has landed and stopped.

If the test fails, confirm that the key belongs to the OpenAI Platform (not a ChatGPT login), that the account has API credit, and that the credit purchase has finished processing.

Only the host needs an API key. Buddy stores it in that Windows user's Credential Manager, never in the mod config or multiplayer messages.

## Talking and actions

Type normally in chat or hold **B** to speak. Every compatible player can talk to him.

You do not need exact wording. Examples include:

| Request | Result |
| --- | --- |
| `Buddy, follow me` | Follows the speaker |
| `Wait here` | Holds position |
| `Check about 15 metres ahead` | Scouts a bounded distance and returns |
| `Bring me some scrap` | Fetches scrap for the speaker |
| `Take scrap back to the ship` | Fetches for ship delivery |
| `Buy three flashlights` | Uses real prices, sales, credits and dropship limits |
| `Open door C7` | Operates the coded facility object |
| `Turn the ship lights off` | Uses the ship-room light control |
| `What's our status?` | Reports current ship and crew state |

`gpt-realtime-2.1-mini` understands the request and chooses from Buddy's small set of typed in-game tools. The host executes the tool and returns its actual result before Buddy replies. The model has no file access, shell, process execution, credential access or arbitrary-network tool.

## One model

Buddy uses **OpenAI `gpt-realtime-2.1-mini` only**.

```text
voice or typed chat
        |
        v
gpt-realtime-2.1-mini
  | understands and speaks
  | requests bounded game tools
        |
        v
host game state -> real tool result -> Buddy's reply
```

There is no separate transcription, chat or text-to-speech model, no provider selector and no exact-command parser. The Realtime session stays connected during play for longer conversational continuity, supplemented by compact in-memory context. Conversation does not persist across game sessions unless response logging is explicitly enabled.

## Multiplayer and voice

- Buddy is host-authoritative; clients accept Buddy state only from the host.
- Every player must have the exact same mod version or Buddy will not spawn.
- Remote push-to-talk audio is sender-bound, version-gated, rate/size/range limited and WAV validated by the host.
- Normal Lethal Company voice chat is restored after Buddy push-to-talk releases the microphone.
- The host generates Buddy's speech once and synchronizes bounded PCM audio to the lobby. Clients never receive the API key.
- His physical body stays absent in orbit and during descent, then appears on exterior NavMesh after a complete landing.

## Story and final stage

The slow-burn arc uses confirmed quota, round and witnessed-death evidence. It saves bounded numeric progress, not dialogue or identities.

`FinalStageHostileSpawns` is enabled for new installs. At the final story stage it may release a current-moon creature near a working crewmate, capped at twice per round with a long cooldown. It never targets someone inside the ship, never spawns another Masked, and cannot be invoked through chat or the model's tools. Disable it unless the whole crew agrees.

Set `SlowBurnHorror = false` to keep the ordinary coworker.

## Privacy and settings

Buddy's native LethalSettings page provides:

- secure OpenAI key save, test and clear;
- microphone and voice volume controls;
- story and final-stage controls;
- separate opt-in response and prompt/context saving.

Response logging is **off by default**. When enabled, it stores typed inputs, Buddy replies, observations and tool results in the bounded host-only `BepInEx/LethalAICrewmate-responses.log`. Voice goes directly to Realtime and is not separately transcribed into that journal. Prompt and sensor context require the additional `SavePromptContext` opt-in. Turning response saving off removes the existing journal.

Buddy's voice is AI-generated. Obtain the crew's consent before enabling any response logging.

Full documentation, troubleshooting, source and security model: https://github.com/TESTYEE-09/Buddy
