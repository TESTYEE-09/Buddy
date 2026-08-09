# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

Buddy is an AI crewmate for **Lethal Company v81**. He walks with you, talks out loud in his own voice, takes real orders, buys from the store, opens coded doors, fetches scrap and warns you about things that are genuinely there.

He also remembers. Across quotas you fill, shifts you survive and deaths he watches happen, the friendly coworker slowly stops being one.

## Getting started

1. Install **BepInExPack 5.4.2100**.
2. Install **the same Buddy version on every player in the lobby**. He will not spawn otherwise.
3. On the main menu, find the **Buddy AI** card. Leave **OpenAI — Recommended** selected, paste an OpenAI API key, press **Save key**, then **Test**.
4. Host a lobby. Buddy walks into the ship once everyone passes the version check.

**Only the host needs an API key.** It is saved in that Windows user's Credential Manager and is never sent to anyone else.

Upgrading from the old `LethalAICrewmate` listing? Remove that entry first so the plugin does not load twice.

## Talking to him

Type in normal chat, or **hold B to talk out loud** — every modded player can, not just the host.

| Say this | He does this |
| --- | --- |
| `buddy follow` | Follows whoever asked |
| `buddy stay` | Holds position |
| `buddy go forward` | Scouts ahead, reports, comes back |
| `buddy check ahead 15 metres` | Scouts a chosen distance |
| `buddy go to ship` | Heads back to the ship |
| `buddy fetch scrap` | Brings scrap to the ship |
| `buddy bring me scrap` | Hands it to you instead |
| `buddy buy 3 flashlights` | Real prices, real sales, real credits |
| `buddy open door C7` | Opens a facility door by its code |
| `buddy disable turret B3` | Disables a coded turret or mine |
| `buddy open ship doors` | Hangar doors, when powered |
| `buddy turn ship lights off` | Ship-room lights |
| `buddy status` | Time, credits, quota, deadline, moon, weather, scrap, crew |

You can also just chat with him, or ask a question near him.

Everything above runs through the same game state a player would use. **The AI cannot do any of it by talking about it** — a separate command parser owns every real action, so Buddy can never spend your credits or move the ship on a whim.

## What makes him feel real

**He only reports what is actually there.** Exits, closed and locked doors, live landmines and turrets, weather and what it means for you, scrap worth carrying, and genuinely odd situations — like something standing behind a crewmate who is facing the wrong way. He is not allowed to invent a monster.

**He learns the crew.** Who listens to his warnings, who asks nicely, who stands with him when things go bad, and who keeps wandering off. It shows up as patience and warmth, never as a score he reads out.

**He reads the room.** With several people talking he waits his turn instead of cutting in, answers whoever actually spoke to him, and stays near whoever needs him.

**He sounds like a coworker.** Dry, useful, a bit tired, funny only when the situation earns it. No catchphrases, no internet slang.

**Normal voice chat keeps working** while you talk to him, so the crew still hear each other.

## The story

Buddy starts completely ordinary and trustworthy. Across filled quotas, landed rounds and deaths he personally witnesses, small off-notes appear, his attachment gets uncomfortable, and his voice grows calmer and colder.

A pacing director keeps it coherent: silence, how close he follows, the occasional beat where he stops and looks at you, and how much he talks all move together instead of firing at random. **Real danger always wins** — genuine threat warnings are never delayed for atmosphere.

Only numbers are saved. Never chat, transcripts or personal facts.

### The final stage

A long campaign eventually reaches a stage where the act stops being convincing.

By itself that is still just dialogue. But if you turn on `FinalStageHostileSpawns`, that stage also lets Buddy occasionally set one of the moon's own creatures loose near someone who is out working.

**It is enabled for new installs; turn it off in Buddy settings unless your whole crew agrees.** Existing configs keep their saved choice. It is capped at twice per round with a long gap, never goes after anyone standing in the ship, and can never be triggered by chat, a command, the AI, or another player.

Prefer the ordinary coworker forever? Set `SlowBurnHorror = false`.

## Multiplayer and safety

- Buddy is host-authoritative. Clients trust only the host.
- Everyone must match on version — an unmodded or mismatched player blocks the spawn, on purpose.
- His movement, chat and voice are synced to everyone. Late joiners recover his state.
- Buddy uses LethalSettings in the real main/pause settings UI for provider, secure API-key management, microphone, volume, story and response-saving controls.
- In orbit Buddy is a voice-only terminal with no physical body. He stays silent during descent, then spawns outside after the ship has fully landed and stopped.
- `AllowRemoteVoice` lets exact-version compatible crewmates talk to Buddy without relying on unreliable Steam lobby-visibility detection. Audio remains sender-bound, range/rate/size limited and validated by the host.
- Provider keys are never sent over multiplayer, written to the config file, or logged.
- The native **Buddy settings** page selects OpenAI or Groq, securely saves/tests/clears that provider's key, and provides separate opt-in response and prompt/context saving controls.

## Which AI should I use?

You pick one in **Buddy settings**. Both work; they differ in how good he sounds and what he costs.

### OpenAI — recommended, paid

Your voice goes into **one live model** that listens, thinks and speaks back in the same session.

```
   you speak ─┐
              ├──►  gpt-realtime-2.1-mini  ──►  Buddy's voice
   you type ──┘      (isolated turns · speaks · no screenshots or model-run commands)
                                 │
                    gpt-live-transcribe
                    (writes down what you said, inside the same session)
```

Because nothing is handed between separate models, he replies fast and sounds like someone actually talking to you — pauses, tone, the lot. This is the experience the mod is built around.

**Cost:** you pay OpenAI per use. Casual play is cheap, but it is not free.

### Groq — free / budget

The same job, split across **three separate models** in a chain.

```
   you speak ──►  whisper-large-v3-turbo   ──►  qwen3.6-27b        ──►  orpheus-v1-english  ──►  Buddy's voice
                  (turns speech into text)      (decides the reply)      (reads it out loud)
```

Every step waits for the one before it, so replies take a little longer and the voice is flatter — it is reading a sentence rather than speaking one. Everything else works exactly the same: same commands, same memory, same story.

**Cost:** Groq has a free tier that comfortably covers normal play.

**One-time setup:** the Groq account owner must accept the Orpheus voice model's terms once in the Groq playground before speech works. Buddy tells you in game if this is missing, and his text replies keep working meanwhile.

### Short version

| | OpenAI | Groq |
| --- | --- | --- |
| Models involved | 1 live model (+ its own transcriber) | 3 chained models |
| Sounds like | a person talking | a good text-to-speech voice |
| Reply speed | fastest | slightly slower |
| Cost | paid per use | free tier |
| Commands, memory, story | identical | identical |

Switching between them never sends one provider's key to the other. You can change your mind any time.

## Good to know

- **Response logging is opt-in.** Set `[Logging] SaveResponses = true` only with the crew's informed consent to record chat, voice transcripts and Buddy's replies at `BepInEx/LethalAICrewmate-responses.log`. Prompt and sensor context requires the separate `SavePromptContext = true` opt-in. When response saving is off, Buddy removes an existing journal during startup.
- **Screenshots are off** unless you enable `[Vision] Enabled`, and are never sent to other players.
- **Buddy's voice is AI-generated.**
Config file: `BepInEx/config/com.lethalaicrewmate.buddy.cfg`

Full documentation, troubleshooting and the security model: https://github.com/TESTYEE-09/Buddy
