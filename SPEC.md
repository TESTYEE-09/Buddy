# Buddy — Current Design Spec

BepInEx 5 plugin for **Lethal Company v81**. Buddy is a host-authoritative AI crewmate backed by OpenAI Realtime.

## Release architecture

- One `netstandard2.1` assembly: `LethalAICrewmate.dll`.
- Pinned game references: `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.
- Harmony patches only; no custom network prefab or asset bundle.
- Buddy uses the game's registered `MaskedPlayerEnemy` NetworkObject after hostile behavior is neutralized.
- Host authority covers spawning, movement, game tools, model calls and generated voice.
- Every connected player must run the exact same release and protocol.
- Clients never receive the host API key.

## Voice-only conversation

Push-to-talk voice is the only Buddy conversation input. Typed Lethal Company chat remains available to the vanilla game but is observed only for social turn-taking bookkeeping. It never triggers Buddy, enters the model or spends API credit.

The host and compatible remote clients capture bounded push-to-talk audio. The host sends it to the single persistent Realtime session. Buddy's generated voice is distributed to compatible clients as bounded PCM audio. Normal Lethal Company voice chat resumes when the push-to-talk key is released.

## OpenAI Realtime

- Model: `gpt-realtime-2.1-mini`.
- Reasoning: `low`.
- Normal response ceiling: 384 output tokens.
- Native Realtime audio input/output; no separate chat, transcription or TTS request path.
- Static system-prompt prefix remains stable for prompt caching.
- Live sensor context is refreshed per turn.
- The active Realtime session retains current conversation context; bounded in-memory context supplements it. Both reset with the gameplay/session lifecycle.
- Buddy has no screen-capture path: the mod cannot read or upload the host's screen.

Normal message audio starts playback after the Realtime output item is identified as a message. Tool-call responses remain buffered until the host-side game result is returned. Buddy therefore gains earlier ordinary speech without allowing unconfirmed tool success to reach players.

## Tools

The model can request only typed host-side tools for:

- follow, stay, return to ship, bounded scouting and scrap fetching;
- ship status, moons and store information;
- store purchases;
- coded facility doors, turrets and mines;
- ship hangar doors and ship-room lights;
- the deliberately capped item request, which requires explicit pleading.

The host executes each tool and returns its real result before Buddy produces the final spoken answer. No file, shell, process, credential or arbitrary-network tool exists.

## Body and movement

Buddy is spawned by the host only after a complete landing settles. He remains a voice terminal in orbit and during descent. Movement uses the neutralized Masked NavMesh agent with follow, stay, return and fetch states. Persistent path failure is required before sampled-NavMesh recovery.

## Contextual autonomy

Periodic unsolicited observations are disabled by default with `ObservationIntervalSeconds = 0`. Confirmed danger and important event callouts remain separate, bounded, host-side behavior. Observations never accumulate ahead of player voice turns.

## Character arc

With `[Character] SlowBurnHorror = true`, confirmed quota cycles, landed rounds and locally witnessed deaths advance a persisted numeric score. Stages are:

- Coworker: 0–2
- OffNote: 3–7
- Unsettling: 8–14
- Cold: 15–27
- Feral: 28+

The arc changes presentation only. Hostile spawning is a separate final-stage host setting, capped and never requested by voice, chat or the model.

## Multiplayer protocol

Clients send only bounded, sender-authenticated voice relay messages and handshake data. The host sends compatible Buddy state, generated audio, captions where enabled by the current client path, and held-item synchronization. Keys never cross the network.

## Privacy

`SaveResponses = false` and `SavePromptContext = false` are the defaults. Opt-in journaling stores voice-turn results, observations and tool results on the host. Voice audio is sent directly to Realtime and is not separately transcribed into the journal. No API key is written to config, logs or multiplayer messages.

## Release gates

CI and `pack.ps1` enforce:

- manifest/csproj/plugin version equality;
- warnings-as-errors compilation;
- source and compiled-DLL secret scans;
- retired-provider/model checks;
- Realtime function-call/result handling;
- release regression checks;
- exact package contents;
- valid icon, ZIP extraction and SHA-256 checksum.
