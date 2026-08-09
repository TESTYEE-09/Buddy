# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

**Buddy** is an AI crewmate for **Lethal Company v81**. He walks with you after landing, listens to push-to-talk voice, follows spoken requests, buys from the store, operates supported ship and facility controls, fetches scrap and reacts to confirmed danger.

Typed Lethal Company chat remains normal game chat and **does not trigger Buddy or use API credit**. Hold **B** to talk to him. Every compatible player can use voice.

## Quick start

1. Install **BepInExPack 5.4.2100** and **LethalSettings 1.4.1**. Thunderstore/r2modman installs both automatically.
2. Install Buddy on every player in the lobby with the exact same version.
3. Open **Settings > Mod Settings > Buddy**.
4. Create an OpenAI Platform API key, add API credit, paste the key into Buddy's settings, then press **Save key** and **Test key**.
5. Host a lobby and hold **B** to speak.

Only the host needs an API key. It is stored in Windows Credential Manager and is never sent to clients.

Buddy uses the **OpenAI API**, billed separately from ChatGPT. See the [OpenAI billing page](https://platform.openai.com/settings/organization/billing/overview) and [API key page](https://platform.openai.com/api-keys).

## Spoken requests and actions

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

Typed chat is intentionally ignored by Buddy. This avoids unnecessary model calls and keeps the Realtime session focused on voice.

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

Reasoning stays at **low** effort. Spoken replies target **2-14 words**: concise but not clipped. Normal responses have a 384-token ceiling so Buddy stays brief and responds quickly. Ordinary message audio starts as soon as Realtime identifies the output as a message. Tool-call audio remains buffered until the real host-side game result returns, so Buddy cannot announce a false success.

The Realtime session stays connected during play for conversational continuity, supplemented by bounded in-memory context. Typed chat never enters this model path. Context resets with the gameplay/session lifecycle and is not durable chat storage.

## Voice and multiplayer

- Buddy's voice is generated once by the host and synchronized to compatible clients.
- Remote push-to-talk audio is sender-bound, version-gated, rate/size/range limited and WAV validated.
- Normal Lethal Company voice chat resumes after Buddy push-to-talk releases the microphone.
- Clients never receive the API key.
- In orbit Buddy is a voice terminal without a body. His body appears outside only after the ship has landed and stopped.

## Story and final stage

The slow-burn arc uses confirmed quota, round and witnessed-death evidence. It saves bounded numeric progress, not dialogue or identities.

Stages are Coworker, OffNote, Unsettling, Cold and Feral. Set `SlowBurnHorror = false` to keep the ordinary coworker.

`FinalStageHostileSpawns` is enabled for new installs. At the final story stage it may release a current-moon creature near a working crewmate, capped at twice per round with a long cooldown. It never targets someone inside the ship, never spawns another Masked and cannot be invoked through chat, voice or model tools. Disable it unless the whole crew agrees.

## Configuration

`BepInEx/config/com.lethalaicrewmate.buddy.cfg`

```ini
[Crewmate]
Name = Buddy
Enabled = true
ChatHearRange = 0          ; 0 = everyone hears Buddy's voice/captions
ChatTriggerRange = 60      ; nearby push-to-talk trigger distance
ObservationIntervalSeconds = 0 ; periodic unsolicited observations off
EnvironmentAwareness = true
SocialAwareness = true

[Character]
SlowBurnHorror = true
ResetSlowBurnProgress = false
DynamicPacing = true
PlayerRelationships = true
FinalStageHostileSpawns = true

[Voice]
Enabled = true
SpokenReplies = true
Volume = 1.25
PushToTalkKey = B
AlternatePushToTalkKey = None
MaxRecordSeconds = 8
InputDevice =
KeepGameVoiceDuringPushToTalk = true
RealtimeVoiceName = ash

[Security]
AllowRemoteVoice = true

[Logging]
SaveResponses = false
SavePromptContext = false

[Vision]
Enabled = false
```

The native Buddy settings page controls the key, microphone, voice, volume, story settings and response saving. The AI panel explicitly identifies the model, low reasoning mode and voice-only input path.

`ObservationIntervalSeconds = 0` is the default. Confirmed danger and important event callouts remain separate from periodic observations.

## Privacy and cost

- Only the host holds an API key and calls OpenAI.
- Typed chat never enters the model or journal.
- Host and compatible remote push-to-talk audio goes to OpenAI Realtime.
- Generated speech is distributed from the host as bounded PCM audio.
- Screenshots are disabled.
- The opt-in journal stores Buddy voice-turn results, observations and tool results. It does not save raw voice audio or separately transcribe voice.
- `SavePromptContext` additionally stores the system prompt and live sensor context.

Obtain the crew's consent before enabling response logging.

## Building from source

Targets `netstandard2.1`, pinned to `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.

```powershell
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1
```

`pack.ps1` builds the DLL, validates the package and creates the Thunderstore ZIP. Generated DLLs and ZIPs are not committed.

Full security model: [SECURITY.md](SECURITY.md). Current handoff: [HANDOFF.md](HANDOFF.md). Design spec: [SPEC.md](SPEC.md).
