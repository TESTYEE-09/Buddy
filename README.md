# Buddy

> An agentic AI crewmate that listens, reasons about the run, uses real game tools, remembers the session and slowly gets stranger.

**Buddy** is a voice-first AI crewmate for **Lethal Company v81**. He is not a menu of scripted voice commands. Talk to him naturally and the Realtime model receives fresh host-side game context, decides whether to answer or use one of Buddy's bounded game tools, then reacts to the real result.

He can follow or wait, scout ahead, fetch scrap, return items to the ship, inspect ship and crew state, check moons and the store, buy equipment, and operate supported facility and ship controls. You do not need to memorize exact phrases or command syntax.

Typed Lethal Company chat remains normal game chat and **does not trigger Buddy or use API credit**. Hold **B** to talk to him. Compatible remote players can use voice when the host's lobby policy allows it.

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

## Quick start

1. Install **BepInExPack 5.4.2100** and **LethalSettings 1.4.1**. Thunderstore/r2modman installs both automatically.
2. Install Buddy on every player in the lobby with the exact same version.
3. Open **Settings > Mod Settings > Buddy**.
4. Create an OpenAI Platform API key, add API credit, paste the key into Buddy's settings, then press **Save key** and **Test key**.
5. Host a lobby and hold **B** to speak.

Only the host needs an API key. It is stored in Windows Credential Manager and is never sent to clients.

Buddy uses the **OpenAI API**, billed separately from ChatGPT. See the [OpenAI billing page](https://platform.openai.com/settings/organization/billing/overview) and [API key page](https://platform.openai.com/api-keys).

## Talk to him like a crewmate

Hold **B** and speak naturally. Buddy is designed to work from intent and current game state instead of matching fixed command phrases.

Ask him to come with you, stay somewhere, check ahead, bring back scrap, take things to the ship, tell you how the run is going, buy useful gear, or operate a supported door, turret, mine, hangar door or light. The model chooses the appropriate bounded tool when one is needed rather than making you phrase the request a specific way.

Each turn is grounded in live host-side context. The host executes supported actions and returns the real result to the model before Buddy speaks, so he can react to what actually happened instead of blindly announcing success. The model has no file, shell, process, credential or arbitrary-network access.

Typed chat is intentionally ignored by Buddy. This avoids unnecessary model calls and keeps the Realtime session focused on voice.

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

The Realtime session stays connected during play for conversational continuity, supplemented by bounded in-memory context. That lets Buddy remember the current session without turning into permanent chat storage. Typed chat never enters this model path. Context resets with the gameplay/session lifecycle.

## Voice and multiplayer

- Buddy's voice is generated once by the host and synchronized to compatible clients.
- Remote push-to-talk audio is sender-bound, version/lobby-gated and rate/size/range limited. Public-lobby access is a separate host opt-in that defaults off.
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

[AI]
ReasoningEffort = low      ; minimal | low | medium | high

[Security]
AllowRemoteVoice = true
AllowRemoteVoiceInPublicLobby = false

[Logging]
SaveResponses = false
SavePromptContext = false
```

The native Buddy settings page controls the key, microphone, voice, volume, thinking level, story settings and response saving. The AI panel explicitly identifies the model and the voice-only input path.

`ObservationIntervalSeconds = 0` is the default. Confirmed danger and important event callouts remain separate from periodic observations.

## Privacy and cost

- Only the host holds an API key and calls OpenAI.
- Typed chat never enters the model or journal.
- Host and compatible remote push-to-talk audio goes to OpenAI Realtime.
- Generated speech is distributed from the host as bounded PCM audio.
- Buddy has no screen-capture path: the mod cannot read or upload the host's screen.
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
