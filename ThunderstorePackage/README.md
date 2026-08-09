# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

Buddy is an AI crewmate for **Lethal Company v81**. He walks with you after landing, listens to push-to-talk voice, follows natural spoken requests, buys from the store, operates supported ship and facility controls, fetches scrap and reacts to confirmed danger.

Typed Lethal Company chat remains normal game chat and **does not trigger Buddy or use API credit**. Hold **B** to talk to him. Every compatible player can use voice.

## Setup

1. Install the same Buddy version on every player. Thunderstore installs BepInExPack and LethalSettings automatically.
2. Add OpenAI API credit at the [OpenAI Platform billing page](https://platform.openai.com/settings/organization/billing/overview). Buddy uses API billing separately from a ChatGPT subscription.
3. Create a key at [platform.openai.com/api-keys](https://platform.openai.com/api-keys).
4. In Lethal Company, open **Settings > Mod Settings > Buddy**, paste the key, press **Save key**, then **Test key**.
5. Host a lobby with the same Buddy version installed for everyone.

Only the host needs an API key. It is stored in Windows Credential Manager and is never sent to clients.

## Talking and actions

Hold **B** and speak naturally. No exact command wording is required.

| Spoken request | Result |
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

The host executes each supported game action and returns the real result before Buddy speaks. The model has no file, shell, process, credential or arbitrary-network access.

## One model

Buddy uses **OpenAI `gpt-realtime-2.1-mini` only**:

```text
push-to-talk voice
        |
        v
gpt-realtime-2.1-mini
  | low-effort reasoning
  | bounded game tools
  | native voice output
        |
        v
host game state -> real tool result -> Buddy's spoken reply
```

Reasoning stays at **low** effort. Spoken replies target **2-14 words**: concise but not clipped. Normal responses have a 384-token ceiling so Buddy stays brief and responds quickly. Ordinary message audio starts as soon as Realtime identifies the output as a message. Tool-call audio remains buffered until the real host-side game result returns, so Buddy cannot announce a false success. The Realtime session stays connected during play for conversational continuity, supplemented by bounded in-memory context. Typed chat never enters this model path.

## Voice and multiplayer

- Buddy's voice is generated once by the host and synchronized to compatible clients.
- Remote push-to-talk audio is sender-bound, version-gated, rate/size/range limited and WAV validated.
- Normal Lethal Company voice chat is restored after Buddy push-to-talk releases the microphone.
- Clients never receive the API key.
- In orbit Buddy is a voice terminal without a body. His body appears outside only after the ship has landed and stopped.

## Story and final stage

The slow-burn arc uses confirmed quota, round and witnessed-death evidence. It saves bounded numeric progress, not dialogue or identities. Set `SlowBurnHorror = false` to keep the ordinary coworker.

`FinalStageHostileSpawns` is enabled for new installs. At the final story stage it may release a current-moon creature near a working crewmate, capped at twice per round with a long cooldown. It never targets someone inside the ship, never spawns another Masked and cannot be invoked through chat, voice or model tools. Disable it unless the whole crew agrees.

## Privacy, cost and settings

Buddy's native LethalSettings page provides:

- secure OpenAI key save, test and clear;
- microphone, voice and volume controls;
- voice-only behavior information;
- story and final-stage controls;
- separate opt-in response and prompt/context saving.

`ObservationIntervalSeconds = 0` by default, so periodic unsolicited observations are off. Confirmed danger and important event callouts remain separate.

Response logging is **off by default**. When enabled, it stores Buddy voice-turn results, observations and tool results in the bounded host-only `BepInEx/LethalAICrewmate-responses.log`. Voice audio is sent directly to Realtime and is not separately transcribed into that journal. Prompt and sensor context require the additional `SavePromptContext` opt-in.

Screenshots are disabled. Buddy does not upload the host's screen.

Buddy's voice is AI-generated. Obtain the crew's consent before enabling response logging.

Full documentation and source: https://github.com/TESTYEE-09/Buddy
