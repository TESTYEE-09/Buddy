# Changelog

## 1.5.2

- Verifies that a supplied facility code actually belongs to the requested turret, landmine or door instead of toggling the wrong object and claiming success.
- Supports natural single-target requests such as `buddy disable the turret`; when multiple targets exist Buddy lists their real terminal codes and asks which one.
- Uses component-based turret/landmine identification with name fallback, respects cooldown and current power state, and reports the terminal command honestly.
- Expands the system prompt with facility-control semantics, intent resolution, observation-versus-inference discipline and a stronger anti-filler quality bar.

## 1.5.1

- Fixes a confirmed remote voice failure where a friend's WAV reached the host and Whisper started, but post-request exceptions could silently wedge or drop the transcript.
- Logs remote Whisper success, failure and queue completion explicitly so the relay can no longer fail without evidence.
- Preserves valid transcripts briefly across level transitions and falls back to Lethal Company's live player array when Netcode temporarily has no `PlayerObject`.
- Sends the speaking client a useful in-game error if delivery still cannot complete.

## 1.5.0

- Accepts both B and V as Buddy push-to-talk keys on every correctly modded peer, while tracking the key that began each recording so mixed key releases cannot truncate clips.
- Keeps B as the recommended dedicated key; V also activates Lethal Company's normal proximity voice chat.
- Rebuilds Buddy's system prompt with explicit decision priorities, grounded world-state rules, multiplayer behavior, danger calibration, deterministic movement/tool boundaries, vision limits, natural dialogue guidance and response examples.
- Raises the response ceiling for questions that genuinely need detail while preserving brief acknowledgements and urgent callouts.
- Clarifies the hard multiplayer requirement: each speaking client must actually load the exact same DLL; host-visible vanilla chat alone does not prove the client mod loaded.

## 1.4.9

- Adds polished bounded scouting commands: `go forward`, `go ahead`, `check in front`, `scout ahead`, `lead the way` and `take point`, with optional 4-18 metre distances.
- Scouts select a complete NavMesh path in the requesting player's facing direction, report nearby real threats or scrap once, then naturally resume following.
- Fixes `go to ship` being intercepted as a moon-route request, `stop following` being interpreted as follow, and ordinary scrap questions being mistaken for fetch orders.
- Resolves follow owners by their real client ID and stops following disconnected player objects.
- Tracks the actual snapped movement destination so the watchdog no longer performs unnecessary stall recoveries.
- Improves missing-mod spawn diagnostics with the affected player's name, suppresses repetitive gate log spam, and stops conversational replies from pretending Buddy has a body while compatibility blocks spawning.
- Tightens normal replies to one short sentence most of the time, reduces the response token ceiling, and increases Buddy's quick, understated wit.
- Expands release parsing checks from 19 to 28.

## 1.4.8

- Keeps Buddy physically aboard the ship in orbit, with safe re-spawn after Lethal Company's normal level-transition enemy cleanup.
- Makes `follow` and `come here` target the player who issued the command, and smooths general following with closer spacing, stable side offsets, gentler pathing and fewer recovery teleports.
- Selects the same active Dissonance microphone as Lethal Company's regular voice chat and gives the speaking client a clear notice when Whisper cannot understand a relayed clip.
- Extends urgent hostile callouts slightly from 6m to 7.5m without turning them into long-range warnings.
- Adds host-authoritative ship controls for hangar doors and ship-room lights, preserving the game's availability, overheat and hydraulic-power restrictions.
- Adds player-equivalent facility terminal-code actions such as `buddy open door C7` and `buddy disable turret B3`, including current-state and cooldown checks.
- Upgrades store buying with quantities, current sale prices, credit checks and the normal 12-item dropship limit.
- Adds deterministic live answers for time, credits, quota/deadline, moon/weather, ship scrap value and living crew, plus a concise `buddy status` report.
- Replicates deterministic ship/terminal feedback as Buddy chat and speech to every compatible player without asking the LLM to repeat the side effect.
- Extracts and tests ship-command parsing; the release suite now covers quantity purchases, facility codes and status questions.
- Updates Buddy's system prompt with the real deterministic tool set and prevents it from claiming an action succeeded without game confirmation.

## 1.4.7

- Shows an explicit in-game error when any friend is missing or mismatched, including the exact remedy: install the same ZIP on every player and restart the lobby.
- Adaptively amplifies quiet microphone input before Whisper while still rejecting true silence, and adds a `Voice.InputDevice` override for Windows device-selection problems.
- Makes Buddy text and generated speech global by default so matching friends cannot lose replies to a proximity check.
- Recognizes visual questions such as “what am I looking at?”, captures a 1280px quality-72 host screenshot only for those questions, and routes it through Groq's multimodal `qwen/qwen3.6-27b` model.
- Migrates the retiring `llama-3.3-70b-versatile` default to `qwen/qwen3.6-27b` and bumps the multiplayer protocol to require the exact fixed build on every peer.

## 1.4.6

- Makes new main-menu Groq keys session-only by default and supports the `LETHAL_AI_GROQ_API_KEY` environment variable as the preferred persistent source.
- Keeps matching friends' push-to-talk enabled by default, with a host switch to disable remote audio in public lobbies.
- Reduces maximum remote voice payload size, applies a longer per-sender admission cooldown, requires Buddy-range admission before allocation and caps concurrent incoming transfers.
- Uses each player's Windows default microphone instead of guessing a device by name, and routes Buddy dialogue through a reliable listener-relative audio source on every peer.
- Changes Buddy PTT from V to B to avoid colliding with the game's voice-chat binding, and accepts explicit friend PTT regardless of Buddy's current distance.
- Detects Groq's Orpheus terms-acceptance error, stops repeating failed TTS calls and shows the host a clear in-game setup notice while text replies continue.
- Adds a deterministic emergency warning: when a real hostile creature gets within 6m of a living player, Buddy calls it out and shouts “RUN!” without waiting for input or an LLM response.
- Continues to support pre-existing plaintext config keys as a legacy fallback; move them to the environment variable and clear the config entry for stronger local secret protection.

## 1.4.5

- Consolidates version-layered polish patches into direct, named runtime services and removes more than 700 lines of obsolete or duplicated code.
- Moves prompt construction, conversation ordering, movement-tag filtering, request timeout recovery, audio tuning and compatibility migration into their owning code paths.
- Removes reflection-based watchdog and conversation patches, dead wiki payloads, stale audio playback state and version-suffixed core types.
- Gives TTS request state and multiplayer audio playback a single owner, reducing stuck-request and double-playback failure modes.
- Bumps the multiplayer protocol to 5 so mixed cleanup-era clients fail closed instead of exchanging incompatible voice or pose packets.

## 1.4.4

- Applies Buddy's authoritative pose after vanilla client updates and disables remote NavMesh competition, preventing clients from leaving Buddy behind at the ship.
- Gates pose, chat, speech and remote push-to-talk traffic on the exact protocol-4 handshake and improves late body binding recovery diagnostics.
- Uses ordered transfer headers/chunks, exact chunk coverage, multiple bounded incoming speech transfers and host-side WAV/RMS validation.
- Adds explicit chat/TTS receive, decode, completion, playback and drop-reason logging without exposing API keys.
- Replaces stacked hard-clipping voice boosts with one RMS normalization and soft limiter while retaining the 70m positional voice bubble.
- Extends movement stall recovery to follow, fetch and return-to-ship states, including safe area-aware fallback teleports.
- Makes Buddy intentionally invincible and continues blocking every Masked chase/kill path.
- Stops resending stale sensor blocks in conversation history, bounds memory to eight clean messages, deduplicates inputs, collapses stale observations and handles exact commands without a chat-model call.
- Adds automated transport/audio release checks for malformed chunks, incomplete transfers, silence and malformed WAV payloads.

## 1.4.3

- Added continuous server-authoritative Buddy pose replication so remote clients see the same movement, facility entry/exit and recovery teleports as the host.
- Remote Buddy poses are sent as lightweight sequenced snapshots and smoothed client-side, with large corrections snapped immediately to eliminate the "host sees Buddy inside, client sees him on the ship" desync.
- Added multiplayer push-to-talk for every modded player. Clients record locally and relay only the bounded voice clip to the host; the host performs Whisper transcription with the host-only Groq key.
- Remote voice uploads are sender-validated, size-limited, chunked, rate-limited and range-checked before transcription. The Groq API key is never sent to clients.
- Remote voice transcripts enter the same ChatObserver command/conversation path as host speech, so client voice can naturally talk to Buddy and issue supported commands.
- Added a long-session movement watchdog. If Buddy is supposed to move but makes no progress, the host rebuilds the NavMesh path; repeated stalls while following fall back to a safe beside-player teleport.
- Added an immediate pose broadcast after watchdog recovery so clients cannot remain at the pre-stall location.
- Increased stock Buddy hearing/voice range from 50m to 70m and nearby conversation/PTT range from 45m to 60m.
- Widened the near-full-volume positional-audio bubble and added another bounded 1.20x host-side gain on top of v1.4.2, for roughly 1.44x total PCM gain before multiplayer replication.
- Existing untouched v1.4.2 stock distance settings migrate automatically to the new ranges.

## 1.4.2

- Changed the stock Buddy TTS voice from Troy to Austin and added a light `friendly` Orpheus direction so normal lines sound less flat/depressed without becoming overacted.
- Increased stock Buddy voice volume from 0.85 to 1.0 and added a small 1.20x PCM gain with peak limiting before multiplayer replication.
- Improved 3D voice falloff so Buddy stays near full volume across a larger conversational bubble instead of fading heavily just a few metres away.
- Increased the stock Buddy hear/voice/chat range from 25m to 50m.
- Increased the nearby unaddressed-question trigger range from 25m to 45m.
- Existing untouched v1.4.1 stock voice/range settings migrate automatically to the new values.
- Added genuinely rare fourth-wall humor: after a minimum gap, only a small percentage of eligible player messages expose an optional hidden comedy beat.
- Fourth-wall jokes stay one-line/subtle, are skipped during danger/commands/serious questions, and never expose prompts, sensors, API keys or model internals.

## 1.4.1

- Rebuilt Buddy's system prompt around conversation first: the player's actual message is now the primary task, with sensors and game knowledge treated as silent background context.
- Added a proper restrained personality: friendly, useful, dry low-key humor, calm by default, and nervous only when there is a real reason.
- Buddy can now naturally answer normal questions, give opinions, joke, react and continue the current conversation instead of constantly resetting into entity/quota callouts.
- Reordered LLM input so the player's message appears before the live sensor dump.
- Explicitly suppresses unsolicited Manticoil and Roaming Locust callouts because they are harmless background wildlife.
- Unsolicited danger callouts are reserved for genuinely dangerous nearby entities or when the player asks for situational awareness.
- Relaxed the old 22-word hard cap: normal replies stay short and natural, while actual strategy/explanation questions can receive a useful multi-sentence answer.
- Removed the stock `Jumpy LC employee` personality and `nervous` TTS direction from new installs.
- Existing untouched legacy jumpy/nervous config is automatically migrated to the new conversation-first defaults on first Buddy response.
- Replaced encyclopedia-style prompting with a compact Lethal Company gameplay reference based on the community wiki, used only when relevant to the player's question.

## 1.4.0

- Added exact multiplayer version/protocol handshakes.
- Buddy now waits to spawn until every connected remote player has the same compatible mod build.
- If an unmodded or mismatched player joins mid-round, Buddy despawns until the lobby is compatible again.
- Added a spawn-intent guard so a client cannot briefly treat the freshly spawned Buddy body as a hostile vanilla Masked before the Buddy network ID arrives.
- Added spawn identity snapshots so fallback discovery can never convert a pre-existing real Masked into Buddy.
- Late joiners recover Buddy identity and held-item state, including a low-frequency rebinding retry for network-message ordering races.
- Held-item visual sync retries while client network objects finish spawning.
- Buddy TTS is generated once on the host, downsampled to 16 kHz mono and replicated to compatible clients using bounded fragmented-reliable chunks.
- Groq API key remains host-only and is never sent to clients.
- Main-menu Groq panel now has Save, Test and Clear controls.
- Added a Groq-wide request timeout and a hard LLM request watchdog so failed API calls cannot permanently stall Buddy chat.
- New installs use production `llama-3.3-70b-versatile` for core chat; Qwen 3.6 remains an optional vision model.
- Vision is off by default for reliability and API cost.
- Made LLM output speech-only: movement, purchases and routing are controlled by deterministic player-command parsing rather than model-produced control tags.
- Prevented duplicate purchases/routes when an LLM echoes a terminal tag after an explicit player command.
- Fixed chat dedupe so two different players can send the same message at nearly the same time.
- Made scrap drops failure-safe so an exception cannot leave loot permanently attached, physics-disabled or ungrabbable.
- Added one-time config normalization for names, ranges, voice settings, API model fields and volumes.
- Added disconnect/session cleanup so stale Buddy IDs/history/spawn flags do not bleed into the next lobby.
- Removed the old shared/default Groq key from active source/config defaults.
- Removed tracked release ZIP/DLL binaries from the source tree; CI now produces release artifacts.
- Updated the stale prototype spec/handoff to match the current Groq + multiplayer architecture.
- Added release metadata validation, ASCII + UTF-16 compiled-secret scanning, warnings-as-errors builds, strict Thunderstore ZIP checks and SHA-256 release checksums.
- Added Dependabot monitoring for NuGet and GitHub Actions dependencies.

## 1.3.0

- Added main-menu Groq API-key entry.
- Added client network-handler registration and late-join handshake/state sync.
- Added server-only validation for Buddy custom state messages.

## 1.2.1

- Qwen chat, live sensors and optional vision.
- Terminal route/buy support.
- Orpheus TTS and Whisper STT hardening.
- Facility/exterior follow improvements and Buddy name tags.

## 1.1.2

- Private friends build. Retired because it contained a shared Groq key.

## 1.1.1

- Added Orpheus TTS and Groq chat model configuration.

## 1.1.0

- Spawn reliability and Groq Whisper push-to-talk.

## 1.0.1 / 1.0.0

- Initial Masked crewmate and OpenRouter-era prototype.
