# Buddy

> An agentic AI crewmate that listens, reasons about the run, uses real game tools, remembers the session and slowly gets stranger.

Buddy is a voice-first AI crewmate for **Lethal Company v81**. He is not a scripted command bot. Talk naturally and the Realtime model receives fresh host-side game context, decides whether to answer or use one of Buddy's bounded tools, then reacts to the real result.

He can move with the crew, wait, scout, fetch scrap, return items to the ship, inspect crew and ship state, check moons and the store, buy equipment, and operate supported facility and ship controls. There is no command syntax to memorize.

Typed Lethal Company chat remains normal game chat and **does not trigger Buddy or use API credit**. Hold **B** to talk to him. Every compatible player can use voice.

## Setup

1. Install the same Buddy version on every player. Thunderstore installs BepInExPack and LethalSettings automatically.
2. Add OpenAI API credit at the [OpenAI Platform billing page](https://platform.openai.com/settings/organization/billing/overview). Buddy uses API billing separately from a ChatGPT subscription.
3. Create a key at [platform.openai.com/api-keys](https://platform.openai.com/api-keys).
4. In Lethal Company, open **Settings > Mod Settings > Buddy**, paste the key, press **Save key**, then **Test key**.
5. Host a lobby with the same Buddy version installed for everyone.

Only the host needs an API key. It is stored in Windows Credential Manager and is never sent to clients.

## Talk to him like a crewmate

Hold **B** and speak naturally. Buddy works from your intent and the current run instead of matching fixed phrases.

You can ask him to come with you, stay somewhere, scout ahead, bring back scrap, take things to the ship, tell you how the crew is doing, buy useful gear, or operate supported doors, turrets, mines, hangar doors and lights. The model chooses the appropriate bounded game tool when one is needed.

Each turn is grounded in live host-side context. The host executes supported actions and sends the real result back to the model before Buddy speaks, so he reacts to what actually happened instead of blindly announcing success. The model has no file, shell, process, credential or arbitrary-network access.

## How the agent works

Buddy uses **OpenAI `gpt-realtime-2.1-mini` only**:

```text
push-to-talk voice
        |
        v
gpt-realtime-2.1-mini
  | live game context
  | configurable thinking level
  | bounded game tools
  | native voice output
        |
        v
host game state -> real tool result -> Buddy's spoken reply
```

Thinking level defaults to **low** and is selectable in Buddy's settings (`minimal`, `low`, `medium`, `high`): lower answers faster and costs less, higher judges tool requests better but pauses longer before speaking. Spoken replies target **2-14 words**: concise but not clipped. Responses are capped at 1200 output tokens, which reasoning and speech share. Ordinary message audio starts as soon as Realtime identifies the output as a message. Tool-call audio remains buffered until the real host-side game result returns, so Buddy cannot announce a false success.

The Realtime session stays connected during play for conversational continuity, supplemented by bounded in-memory context. Buddy can therefore remember the current session, but this is not permanent chat storage. Typed chat never enters this model path.

## Voice and multiplayer

- Buddy's voice is generated once by the host and synchronized to compatible clients.
- Remote push-to-talk audio is sender-bound, version/lobby-gated and rate/size/range limited. Public-lobby access is a separate host opt-in that defaults off.
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

Buddy has no screen-capture path: the mod cannot read or upload the host's screen.

Buddy's voice is AI-generated. Obtain the crew's consent before enabling response logging.

Full documentation and source: https://github.com/TESTYEE-09/Buddy

## Project status

Active development is **paused** as of August 2026, with 5.1.3 as the intended stable release.
The mod is feature-complete for what it set out to do and is not abandoned - it is simply not
being worked on day to day.

Contributions are welcome and the project is open to anyone who wants to build on it. Pull
requests are the best way in. Two things are worth knowing before you start:

- `dotnet run --project tests/ReleaseChecks -c Release` must pass. It asserts on safety
  invariants and on exact system-prompt content, so a prompt change that breaks character or
  weakens a safety rule fails the build rather than shipping quietly.
- Nothing may speak for Buddy except the model. There are no hardcoded lines, and the checks
  enforce that. Give the model a fact and let it choose the words.
