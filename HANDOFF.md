# HANDOFF — Buddy v4.1.0

## Current status

Buddy is a BepInEx 5 mod for **Lethal Company v81** adding a host-authoritative AI crewmate named Buddy. The released baseline is v4.1.0. It uses voice-only conversational input with concise 2-14 word spoken replies.

Release gates remain:

- compile against `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`;
- warnings-as-errors compilation;
- source and compiled-DLL secret scans;
- manifest/csproj/plugin version equality;
- exact Thunderstore package whitelist;
- ZIP extraction validation;
- SHA-256 checksum;
- release checks and GitHub Actions success.

## Architecture

- Buddy uses the game's registered neutralized `MaskedPlayerEnemy` NetworkObject.
- The host owns spawning, movement, game tools, Realtime calls, voice generation and multiplayer authority.
- Every player must run the exact same Buddy release and wire protocol.
- Clients never receive the host's API key.
- Buddy's generated voice is replicated as bounded PCM audio to compatible clients.
- Typed Lethal Company chat is observed only for social turn-taking bookkeeping. It never triggers Buddy, never enters the model and never spends API credit.
- Push-to-talk voice is Buddy's only conversational input. Host and compatible remote players use the bounded voice relay.

## OpenAI Realtime path

- Model: `gpt-realtime-2.1-mini`.
- One persistent host WebSocket session handles voice understanding, reasoning, native voice output and bounded host-side tool calls.
- Reasoning effort is the host setting `[AI] ReasoningEffort` (`minimal`/`low`/`medium`/`high`), default `low`. It is validated against that list before it reaches the session config.
- Normal spoken replies target 2-14 words and are capped at 1200 output tokens, shared between reasoning and audio.
- Periodic unsolicited observations default to off with `ObservationIntervalSeconds = 0`; confirmed danger and important event callouts remain separate.
- Normal message audio begins playback after Realtime identifies the output item as a message. Tool-call output remains buffered until the host executes the game action and returns the real result, preventing false success audio.
- The static prompt prefix remains stable for input caching. Live game context is refreshed per turn.
- Conversation context is held in the active Realtime session with a bounded in-memory supplement. It resets when the gameplay/session lifecycle resets and is not durable chat storage.
- Buddy has no screen-capture path: the mod cannot read or upload the host's screen.

## Voice-only behavior

Hold **B** and speak naturally. Typed chat continues to work for the vanilla game but Buddy does not answer it. `ChatTriggerRange` now controls nearby push-to-talk triggering, not typed questions.

The voice path supports follow, stay, return to ship, scouting, scrap fetching, ship status, moons, store purchases, coded facility controls, ship controls and the capped pleading item request. Tool results are returned to the model before the final spoken reply.

## Story and relationships

The slow-burn arc persists bounded numeric progress in the current save. Score comes from fulfilled quotas, landed rounds and deaths Buddy locally witnessed. Stages are Coworker, OffNote, Unsettling, Cold and Feral. The final hostile-spawn behavior remains separately gated by `FinalStageHostileSpawns`.

Relationships store bounded numeric bonds only. Time-together grants use the deliberately slower v3.7.4 timing.

## Privacy and logging

Only the host calls OpenAI. `SaveResponses = false` and `SavePromptContext = false` remain the defaults. When enabled, the host journal stores voice-turn results, observations and tool results; voice audio is sent directly to Realtime and is not separately transcribed into the journal. Obtain the crew's consent before enabling logging.

## Build and verify

```powershell
 dotnet build src/LethalAICrewmate.csproj -c Release
 dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj -c Release
 powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1
```

Generated DLLs and ZIPs are ignored by Git. CI validates the release package, source/DLL secrets, versions, icon, checks and checksum.

## Next release smoke test

1. Hold B and speak a normal conversational line; audio should begin before the complete response has finished generating.
2. Hold B and request a tool action; pre-tool preamble audio that is still silent must be dropped, and an already-audible preamble must NOT be cut mid-sentence (the confirmed line follows it).
3. Say "grab that bolt" with loose bolt scrap within 25m; Buddy should call `move_buddy` with `item_name` "bolt" and never demand a name or distance first.
4. Say "grab the nearest scrap" with loose scrap nearby; the tool must be called without `item_name` and pick the best scrap itself.
5. Say "spawn a jetpack" twice; both refusals must be the same one-liner with no capability explanation.
6. Check that spoken replies never contain "sensor", "context", "list says" or other context-language.
7. Verify `ObservationIntervalSeconds = 0` produces no periodic observations while confirmed danger callouts still work.
8. Run the release build and all release checks before versioning the next package.

## Release flow

1. Bump manifest, csproj and `Plugin.ModVersion` together.
2. Update the current changelog section and package README.
3. Run build, release checks and `pack.ps1`.
4. Commit the release changes and push the release branch to `main`.
5. Wait for the main workflow to pass.
6. Verify the generated GitHub release ZIP and `SHA256SUMS.txt`.
