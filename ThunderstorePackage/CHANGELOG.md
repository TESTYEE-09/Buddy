# Changelog
## 4.4.0
One action, one reply, and it sounds like Buddy.

- Buddy no longer speaks twice for one action. A tool turn used to produce a spoken preamble
  and then a second line after the result, because playback started as soon as Realtime
  labelled an output item a "message" - which is also true of a preamble emitted before a
  function call in the same response. Audio on any tool-capable turn is now held until the
  response is finished and known to be a plain message.
- Actions no longer sound pre-recorded. The function result was handed to the model as a
  finished English sentence ("Fetching scrap for the ship."), which it read back word for
  word, so the line players heard was a hardcoded string rather than Buddy talking. Movement
  results are now terse status data ("ok: state=fetching_scrap deliver_to=ship") and are
  labelled private status the model must answer in its own words.
- Talking about a job no longer starts the job. "Ready to get all the scrap?" triggered a
  fetch. A new WHEN TO ACT section separates orders from questions, plans, commentary and
  hypotheticals, and the examples now cover the exact phrasings that misfired.
- Buddy no longer goes silent after doing what he was asked. A response that returned text
  without audio threw and discarded the whole turn - including an action the game had already
  performed. It is now delivered as chat and logged, and the follow-up response after a tool
  result explicitly asks for speech.
- Rewrote the tool half of the system prompt around one rule: acting and speaking are separate
  acts that never share a turn. Call silently, read the status privately, then say one short
  thing of your own.
- Six new release checks covering all of the above; nothing in the existing 69 was weakened.

## 4.3.0

- **Buddy turns things down like a person now.** "I can't kill it without a direct action tool" was a
  real answer to "kill the bug on my head". He no longer describes his own plumbing to explain a refusal:
  a thing he does not do gets a brush-off with attitude - "Nah, couldn't be bothered.", "You've got
  hands.", "Charge it yourself." - and the character arc colours how cold that brush-off sounds.
- **Fixed Buddy inventing game mechanics to justify a refusal.** Asked to come into the facility he
  replied "Need the elevator code or a confirmed elevator entrance first", then repeated it twice. There
  is no elevator code in Lethal Company. 4.1.0's honesty rule required a refusal to name a real missing
  prerequisite and forbade inventing a limit, but never offered plain declining as an option - so when
  the answer was simply "I don't do that", the only exit left was to invent a prerequisite. Refusing
  because you cannot be bothered is now explicitly honest, and inventing a missing code, permission or
  confirmation is now explicitly a lie.
- **Removed a capability the prompt promised and the game never had.** The contract told Buddy his body
  could "enter the facility"; no such action exists in the tool layer, which is what left him improvising
  entry requirements. The contract now lists exactly what he can do, states that it is the whole list,
  and states plainly what he does not do - fighting, healing, reviving, carrying, recharging, driving,
  weapons, facility entry on command, teleporting.
- **Refusal rules live in one place.** Guidance had drifted across three sections that partly contradicted
  each other. There is now a single SAYING NO section separating three cases: can do it (do it, say
  almost nothing), can do it but something real is missing (name the real thing in one line), and does
  not do it at all (refuse in character, never explain).
- **A stronger personality cannot become laziness.** Buddy has opinions, grudges, moons he hates and
  permission to be a bit of a bastard - but "disinterest is never a reason to skip an action you can
  perform: you grumble and you still do it."
- Banned the vocabulary that leaked the implementation: tool, function, feature, ability, capability,
  system, sensor, parameter, "not set up to", "there isn't a", "that's not supported". The old ban list
  covered "capability" and "authorization" but not "tool", so the model simply used the words left open.
- Four new security regression checks lock all of this in. No existing safety invariant was weakened:
  no attacking, no sabotage, no fabricated evidence, no overriding safety, and hunting stays gated behind
  the Feral stage plus its own explicit host opt-in.

## 4.2.0

- **Buddy's replies no longer get cut off mid-word.** 4.1.0's "softer" preamble flush only spared a
  preamble that had already been audible for half a second; anything younger was still hard-cut, ring
  buffer wiped and audio source stopped. Because playback does not begin until the cushion fills while
  the tool runs in parallel, almost every tool turn landed inside that window, so the fix released as
  the cure was still chopping the common case. Once any audio has reached the speaker the line now
  always finishes. Truthfulness is unchanged: the unconfirmed transcript is still discarded and Buddy
  still re-answers from the real tool result, so a spoken preamble is simply followed by the correction.
- **Fixed a false starvation that stalled every reply.** The last audio callback of any finished line
  drains the ring by definition, which set the underrun flag with nothing actually wrong. The flag was
  never cleared when playback stopped normally, so the next response's first chunk reported a buffering
  fault that never happened and widened the cushion. Over a few tool turns it pinned at the 1.5s maximum
  and Buddy sat silent for a second and a half before speaking. The flag is now cleared when a line ends.
- **"Buddy's live voice stream stopped" now says why.** Four unrelated causes - no reservation, a
  superseded stream, a stream whose turn already failed, and overflowing the input queue bound - all
  surfaced that one line with nothing in the log to tell them apart. Each now logs its own reason and
  the stream id, so the next occurrence is diagnosable from an ordinary log.
- **The character arc moves roughly twice as fast and each stage bites harder.** Feral needed about
  seven quota cycles and most crews never saw it, so the back half of the arc was written but unplayed;
  the thresholds drop from 3/8/15/28 to 3/6/10/16. The opening stage still costs three points, so Buddy
  is never ominous the moment he spawns. Every stage past that is rewritten more possessive and more
  openly wrong - Cold now counts the living out loud, Feral talks about the crew as things it keeps.
  All existing safety rails are untouched: no attacking, no sabotage, no fabricated evidence, no
  overriding safety, and hunting stays gated behind Feral plus its own explicit host opt-in.

## 4.1.0

- **Buddy can fetch the scrap you name.** The fetch tool now takes an optional `item_name` straight from
  the speaker's words: "grab that bolt" targets the bolt, while asking for "the nearest scrap" still picks
  the best one automatically. He no longer stalls with "I need the name before I can grab it" - if he
  genuinely cannot find the named item, the tool result says so and he reports that in one line.
- **The context now lists loose scrap by name, value and distance** (up to the six nearest), so Buddy
  knows what is actually lying around instead of just a count and no longer has to invent or guess items.
- **No more invented inabilities.** "I'm not set up to carry gear to the ship" was spoken when the fetch
  tool exists to do exactly that; the contract now forbids claiming a lack the tools cover and forbids
  demanding information the context already provides. A refusal must name a real missing thing.
- **Sensor internals never reach the crew's ears.** "Monsters are off the sensor list" and "the sensor
  said you were far from me" leaked internal vocabulary into speech; quoting the context is now banned and
  reported facts are phrased as his own observation.
- **Tool preambles are dropped only while still silent.** The v4.0.0 flush cut everything before a tool
  result, including mid-sentence, which read as clipped replies. Playback now only discards preamble audio
  that has not become audible (or is under half a second old); a preamble the crew is already hearing is
  left to finish naturally and the confirmed line follows.
- **Escalation of a refusal is shown, not just banned.** "Ask nicely." twice is now an explicit example,
  with the second ask repeating the same line and never explaining capabilities.

## 4.0.0

- **Buddy stays himself in first person.** In real sessions Buddy sometimes narrated his own actions from
  outside - the exact line was "He's coming up. He'll check what's ahead and report back" - as if watching
  himself work. The personality contract now requires 'I'/'me' for everything he does, says or reports;
  self-reference by name, 'he', 'the crewmate' or 'the AI' is banned, and narrating one's own actions is a
  hard failure. The per-turn context now opens by naming who he is and whom he is answering, so every reply
  is anchored in his own body.
- **Unconfirmed tool preambles are cut, not played.** Buddy spoke before his actions were confirmed; that
  audio is now flushed the moment a tool result arrives, so the crew only hears the line spoken after the
  game actually confirmed the action. No claim can finish playing before it is true.
- **Refusals stay one line.** A captured reply said "I need a distance, or a specific target. I can't just
  pick a whole hallway" - a two-sentence capability lecture. The refusal rule now states the missing thing
  and stops: no second sentence, no list of what he could do instead.
- **Conversation memory now shows both halves.** Earlier turns include Buddy's own past replies, so he can
  stay consistent with what he already said instead of guessing from the crew's lines alone.
- Tool acknowledgements are explicitly first person ('Flashlight's yours.', 'Right behind you.'), with
  counter-examples added for the exact failure modes above.
- Version 4 baseline: cumulative 3.7.6-3.8.0 voice and streaming fixes (continuous gapless stream,
  adaptive cushion, faster first word, opening-chunk release, live push-to-talk) are locked in with this
  behavior release.

## 3.8.0

- Buddy's push-to-talk is now live speech streaming instead of a recorded upload. Microphone audio
  is streamed to the Realtime session in bounded 100 ms chunks while the key is held, and releasing
  the key only commits the buffer and starts the reply. Buddy starts answering a bound sooner and
  no longer waits for the whole WAV to finish recording.
- Multiplayer voice uses the same live transport. Clients stream 16 kHz PCM chunks to the host over
  one ordered fragmented pipeline; the host validates identity, range and chunk order, keeps
  rate/size limits, and remains the only peer with the API key. A bumped protocol version keeps the
  new wire format out of older lobbies.
- Closes a stale-cancellation race: interrupting Buddy while he is mid-reply could cancel the next
  push-to-talk turn or wipe its freshly streamed audio. Cancellation now re-checks its target under
  the send lock and can no longer touch a newer turn.
- Push-to-talk recovers gracefully instead of spamming errors if the microphone position wraps or
  the live stream is interrupted mid-hold: the capture is aborted cleanly with a tip instead of
  throwing every frame until the key is released.

## 3.7.8

- Fixes Buddy still being cut off mid-sentence. 3.7.7 could recover from a starved audio buffer but
  had to learn the right cushion by failing first, so the opening replies of every session were cut
  while it healed. Playback now starts on a cushion that survives the initial burst, and the
  re-buffering warning is logged at info level so a genuine stumble is visible without debug logging.
- Buddy no longer renames what he bought. He was reporting a plain Flashlight as a "pro flashlight" -
  a different, more expensive store item - so the crew was told they owned gear they did not. Item,
  moon, door and creature names are now repeated exactly as the game reports them.
- A greeting gets a greeting back. Buddy no longer answers "morning" by opening a conversation or
  asking what is on the shift list.

## 3.7.7

- Fixes Buddy cutting out mid-sentence. 3.7.6 started playback on a fixed 0.14 s lead-in, so any late
  chunk left the audio thread with nothing to play and it padded the line with silence. Playback now
  detects that starvation and permanently widens its cushion for the session, so a good connection
  stays fast and a jittery one settles into gapless speech after the first stumble.
- Buddy starts talking sooner. The opening chunk of a reply is now ~100 ms instead of 250 ms, and the
  rest of the line streams in larger 400 ms chunks that stay comfortably ahead of playback.
- New **Thinking level** setting in Buddy's settings page (`[AI] ReasoningEffort`): `minimal`, `low`
  (default), `medium` or `high`. Minimal answers fastest and costs least; higher levels judge tool
  requests better but leave a longer pause before Buddy speaks. Host-only.
- Settings cleanup: the `[Vision]` section is gone. It was a reserved, permanently-off screenshot
  toggle with no working path behind it, and the capture code has been removed outright.

## 3.7.6

- Fixes replies being cut in half and stuttering. Streamed speech now plays through one continuous
  buffer instead of a three-clip queue that silently discarded audio whenever Buddy generated faster
  than he could talk, and each chunk no longer carries its own leading silence.
- Raises the response ceiling from 384 back to 1200 tokens. Reasoning and audio share that budget, so
  384 could end a reply mid-word.
- Clients no longer drop parts of a sentence: the concurrent audio-transfer cap fits a streamed reply.
- Buddy starts talking sooner: audio is released in 250 ms chunks and the session is no longer
  reconfigured before every turn.
- Cheaper long sessions: instructions and tool definitions are sent only when they change, so the
  prompt cache survives the whole session, per-turn state arrives as its own conversation item, and an
  explicit retention-ratio truncation policy bounds input cost.
- A response interrupted mid-stream no longer poisons the next turn.
- The `Crewmate.Personality` setting is applied again; it had no effect on Buddy's prompt.

## 3.7.5

- Sets the conversational reply target to 2-14 words: concise without forcing clipped one-word answers.
- Keeps typed chat vanilla-only, with voice push-to-talk as Buddy's only conversational input.
- Retains low Realtime reasoning, the 384-token response ceiling, 500 ms ordinary-audio chunking and confirmed-result buffering for tool calls.

## 3.7.4

- Rewrites the system prompt again: refusals are one short line naming the missing thing and never escalate into lectures; contract-speak ('valid action request', 'supported mechanism', 'capability', 'unit') is banned; no offer, menu or 'say the word' closers, including during danger; brief out-of-game banter stays pointed at the game.
- Item spawns now require genuine pleading: only explicit 'please' or begging calls the spawn tool; plain requests are refused with one line.
- Facility door, turret and mine numbers are used as their terminal codes directly, so 'open door D6' no longer demands a separate code.
- Store purchases now work in orbit, on the ship and on the moon surface; only buying from inside the facility is blocked.
- Relationships build much more slowly: time-together grants every 5 minutes instead of every 90 seconds, so trust no longer spikes after one short run.

## 3.7.3

- Adds a selectable OpenAI Realtime voice to Buddy settings: Ash is the default, and alloy, ballad, coral, echo, sage, shimmer and verse are also available. The host picks the voice; clients hear it, and a change applies from the next spoken reply.
- Replaces the mod icon with the new BUDDY artwork.

## 3.7.2

- Rewrites the system prompt around a real coworker: default 2-8 word replies, a hard ban on canned filler and helper-speak, no offers/menus/"what next?" closers, no therapist mode, and short fun out-of-game banter that stays pointed at the game. Relationship warmth no longer scales how much Buddy volunteers.
- Clarifies the OpenAI API setup: API billing is separate from ChatGPT, the current prepaid minimum is $5 USD, automatic recharge can be disabled, and only the host needs a key.
- Adds direct links and plain-language troubleshooting for billing, API-key creation, secure in-game saving and key testing.

## 3.7.0

- Replaces the split provider and deterministic phrase-command architecture with one `gpt-realtime-2.1-mini` session for typed chat, voice understanding, reasoning, native speech and function calling.
- Lets Buddy understand natural requests and call bounded host-side game tools for movement, status, moons, purchases, facility controls, ship controls and the deliberately capped item request. Tool results are returned to the model before it speaks, so it cannot honestly report success before the game accepts an action.
- Removes the separate transcription model, Groq pipeline, model/provider selectors and exact-command parsers. Voice audio goes directly to Realtime and is not separately transcribed into response logs.
- Keeps the Realtime connection alive during play for substantially longer conversational continuity, with compact in-memory context as a supplement rather than a replacement.
- Limits model tools to typed in-game actions. Buddy receives no file, shell, process, credential or arbitrary-network capability; the OpenAI key remains host-only in Windows Credential Manager or the host environment.
- Rewrites the system prompt around direct, brief, grounded conversation; low unsolicited chatter; natural tool use; rare situational swearing; and real tool-result truthfulness.
- Updates the main-menu settings, README, Thunderstore description, security model and release checks for the single-model architecture.

## 3.6.1

- Replaces the cut-off custom overlay with a native LethalSettings page in the main and pause menus, including provider, secure API key, microphone, volume, story and response-journal controls. The editable personality box is removed.
- Completely rewrites Buddy's shared system prompt from real saved-session failures: direct answers, no exit/safety fixation, ordinary conversational repair, harmless banter, useful game knowledge, player-centred sensor facts, and no robotic capability lectures.
- Adds live crew alive/dead status and Buddy area/distance to context, and centres nearby-entity distances on the player who asked instead of always on Buddy.
- Stops long context from replaying Buddy's own bad prior replies; it retains up to 40 crewmate inputs for references and continuity.
- OpenAI Realtime now transcribes before responding, executes sender-authenticated voice commands on the Unity thread, feeds the confirmed result into the reply, and raises the audio-inclusive output ceiling from 256 to 1024 tokens to prevent mid-sentence cutoffs.
- Keeps Buddy voice-only in orbit, silent during descent, and spawns his physical body on exterior NavMesh only after the ship has fully landed and stopped.
- Removes unreliable Steam lobby-visibility gating so exact-version friends can speak and use sender-bound deterministic voice commands; remote voice retains handshake, range, rate, size and audio validation and remains host-disableable. Spoofable vanilla typed chat cannot authorize state changes.
- Fixes facility/ship area synchronization, drives available walk/run animator parameters, increases voice loudness, prevents the beginning of Realtime speech being clipped, and plays each Realtime reply as one continuous clip.
- Preserves bounded in-memory conversation context across moon trips, while never writing it to disk unless the response journal is explicitly enabled.
- Makes replies shorter and more natural, permits rare situational swearing, suppresses ordinary wildlife/random chatter, and applies a two-minute per-monster danger-callout cooldown.
- Removes the hold-B overlay and exact-command lecturing; Groq turns discard stale queued speech when a new player turn begins.
- Enables the bounded final-stage hostile-spawn story feature for new installs and adds its toggle to native Buddy settings; existing configs retain their saved choice.
- Closes the v3.5.1 security review: model game-action tools are removed, OpenAI turns use isolated sessions, and player chat cannot capture screenshots.
- Changes the alternate Buddy push-to-talk key from the game's normal `V` voice key to `None`.
- Removes raw player chat, names, transcripts and provider bodies from ordinary logs; response/prompt journaling remains explicit opt-in with immediate deletion when disabled.
- Rate-limits and sanitizes compatibility hellos, caps remote identity/pose/audio queues, uses direct spawned-object lookup, range-gates remote voice before allocation, and rejects unexpected model tool-call events.
- Removes arbitrary terminal fallbacks, fixes `mine` substring matching and empty-name matching, sanitizes prompt/HUD text, validates spawn-intent vectors after handshake, and closes RIFF integer-overflow and journal UTF-8 trimming gaps.
- Hardens CI with read-only build permissions, non-persisted checkout credentials, reachable-history secret scanning and a separate write-scoped release job.

## 3.5.3

- Expands the main-menu card into a proper Buddy settings panel with provider selection, secure API-key save/test/clear controls, and separate response-journal and prompt/context consent toggles.
- Disabling response saving from the menu also disables prompt/context saving and immediately removes the existing journal.

## 3.5.2

- Restores raw response logging and prompt/sensor context to explicit opt-in defaults and prevents upgrades from silently enabling either setting.
- Removes an existing host response journal on startup while response saving is disabled.
- Neutralizes control characters and escapes quoted journal fields to prevent forged or ambiguous log entries from player-controlled text.

## 3.5.1

- Rewrote the Thunderstore package description for players rather than modders: what Buddy actually does, the story and its opt-in final stage, and a plain-language comparison of the two AI providers with diagrams of OpenAI's single live model and Groq's three-model chain.

## 3.5.0

- **Individual player relationships.** Buddy now treats each crewmate differently based on what he has actually observed: commands honoured or rejected, politeness, danger shared, deaths he witnessed, and who keeps walking off without him. Storage is deliberately minimal — at most eight sets of three small bounded numbers per save, keyed by a 16-bit non-reversible digest. No names, Steam IDs, chat or transcripts are ever written to disk, and none of it is replicated to clients. Toggle with `Character.PlayerRelationships`.
- **Dynamic horror pacing director.** Silence, follow spacing, staged watching beats and how much Buddy talks are now driven by one plan that reads the campaign stage and live tension together, instead of firing independently. Confirmed danger always outranks it: deterministic threat callouts are never delayed or suppressed. Toggle with `Character.DynamicPacing`.
- **Richer environmental awareness.** Buddy can now see confirmed exits, closed and locked doors, placed turrets and live landmines, weather with its practical consequence, and genuinely unusual entity situations such as something stalking a crewmate who is facing the other way. Unprompted reactions carry long per-kind cooldowns so this adds detail rather than chatter. Toggle with `Crewmate.EnvironmentAwareness`.
- **Multiplayer social intelligence.** Buddy tracks who is speaking, waits his turn when the humans have the floor, answers the person who actually addressed him, and re-acquires whoever currently needs him rather than whoever is nearest. Speaker identity always comes from the host's own player list, never from anything a message claims. Toggle with `Crewmate.SocialAwareness`.
- **Normal voice chat keeps working while you talk to Buddy.** Buddy's push-to-talk shares Unity's global microphone with the game's voice chat, which previously left the game's capture stopped afterwards, so the crew stopped hearing that player. Buddy now restores the game's capture on release and never leaves the speaker muted. Toggle with `Voice.KeepGameVoiceDuringPushToTalk`.
- **New final story stage.** A long campaign can now push Buddy past Cold into a final stage where the performance stops being convincing. At that stage only, and only if the host explicitly enables `Character.FinalStageHostileSpawns` (**off by default**), Buddy will occasionally release one of the current moon's own creatures near a working crewmate. It is host-only, capped at two per round with a seven-minute interval and a delay after landing, never targets anyone standing in the ship, never spawns another Masked, and cannot be requested by chat, a terminal command, a model tool call or any remote player.
- **The response journal is now on by default and records enough to tune Buddy.** `Logging.SaveResponses` defaults to true and existing configs are migrated on with a loud startup warning. Unprompted observations now log the evidence that triggered them instead of appearing as replies to nothing, and `Logging.SavePromptContext` additionally records the exact system prompt (once per change, at most once a minute) and the live sensor block behind each turn, so a bad reply can be traced to the prompt or to what Buddy could actually see. The journal cap is raised from 2 MB to 8 MB. **It records what your crewmates say — set `SaveResponses = false` if anyone in your lobby has not agreed to it.**
- Release checks now lock the final-stage gates, the relationship storage bounds, prompt-name truncation, turn-taking, and the rule that pacing can never silence a danger callout.

## 3.0.1

- Fixed a vulnerability where a modded client in a public lobby could supply the host's player ID in ordinary chat and gain the host exemption for restricted state-changing commands (buy, route, spawn, ship and facility controls). Vanilla chat identifiers no longer grant host trust on any path, including the OpenAI Realtime tool call route.
- Remote state-changing requests now require a verified friends/invite-only lobby or an explicit host opt-in, and fail closed when lobby visibility cannot be confirmed. Read-only status, store and moon queries remain available.
- Legacy plaintext provider keys are migrated into Windows Credential Manager and removed from the config file unconditionally, including when secure storage fails.
- Added release checks locking the public-lobby authorization behaviour and the legacy key cleanup.

## 3.0.0

- Establishes the v3 gameplay baseline for real host-and-client testing before system-prompt refinement.
- Removes the premature 14-second facility/exterior teleport path. Area mismatch now requires three spaced path rebuild attempts and at least 20 seconds of continuous failure before recovery.
- Keeps every recovery destination sampled onto a valid NavMesh and retains the existing host-authoritative pose sync.
- Failed transition recovery no longer clears its evidence timer; retries remain bounded by a cooldown.
- Adds release checks locking the transition threshold to the ordinary emergency-recovery threshold.

## 2.8.0

- Added believable crewmate fetch routines: Buddy now weighs scrap value against travel distance instead of blindly taking the closest object.
- Personal requests such as `bring me scrap` return the item to the requesting player; ordinary fetch commands keep the established safe ship-delivery behaviour.
- Buddy briefly waits at a nearby closed door when regrouping instead of repeatedly pressing into it. This does not grant him authority to unlock doors or operate protected systems.
- Kept all routine decisions host-authoritative and reused the existing bounded item attach/drop replication.
- Added release-policy coverage for scrap selection, personal handoffs and door-wait boundaries.

## 2.7.0

- Rebuilt follow movement around NavMesh-safe walking, distance-based catch-up speed and slower turning. Raw-transform flight is removed.
- Path recovery now rebuilds repeatedly and waits for a persistent failure before any emergency teleport. Facility transitions hesitate before a last-resort recovery.
- A followed player's death no longer causes an instant target switch or teleport. Buddy pauses, only treats a nearby same-area death as witnessed, walks to another crewmate, and may report what he actually saw.
- Compatible late joins keep the existing Buddy body. The host waits through the handshake grace period and continues pose sync to already-compatible peers; confirmed mismatches still fail closed.
- Added rate-limited, model-generated contextual conversation for facility transitions, returning to ship, long travel, separation, valuable scrap and quiet downtime. Recent player speech always takes priority.
- Horror progression can change intentional idle gaze and spacing, but never uses broken navigation as a scare. Global death counts no longer give Buddy knowledge of unwitnessed deaths.
- Release checks cover emergency recovery thresholds, witnessed-death evidence, catch-up speed and autonomous-speech priority.

## 2.6.0

- Makes OpenAI Realtime the clear recommended/default Buddy experience in a redesigned, compact main-menu setup card; Groq is presented separately as the free/budget option.
- Consolidates the complete OpenAI path onto `gpt-realtime-2.1-mini` for typed conversation, push-to-talk, native Ash voice, image questions and bounded host-side tool calls.
- Uses `gpt-live-transcribe` only for live input transcription within the Realtime pipeline and removes the former separate OpenAI chat/TTS fallbacks from active code and config.
- Updates Groq to a separate `qwen/qwen3.6-27b` + `whisper-large-v3-turbo` + `canopylabs/orpheus-v1-english` pipeline.
- Migrates old provider model defaults without changing multiplayer, gameplay commands, security boundaries, synchronization or character-arc state.
- Migrates legacy plaintext provider keys into Windows Credential Manager and removes their old configuration controls after a successful secure save.
- Adds release checks that lock the provider split and make OpenAI the fail-safe default.

## 2.5.0

- Renames the public mod and repository branding to **Buddy**, with a new minimalist human coworker icon and the tagline: "A useful crewmate with a memory. The longer you work together, the stranger it gets."
- Adds an opt-out slow-burn horror character arc inspired by the emotional shape of companion horror: Buddy begins as the trustworthy dry coworker, develops subtle off-notes, becomes quietly unsettling, and eventually turns cold and possessive.
- Progress is host-authoritative and grounded in confirmed campaign evidence: fulfilled quota cycles, new landed rounds and witnessed crew deaths. Numeric progress and its quota baseline persist in the current Lethal Company save; no dialogue or personal data is stored. Reloads cannot double-count a quota.
- Adds sparse deterministic character beats for real round, quota and death events, with a 150-second cooldown. The early game never forces ominous lines.
- Adapts both conversation policy and OpenAI TTS direction by stage, moving from familiar coworker delivery to restrained psychological horror without a monster voice or cartoon-villain writing.
- The arc is presentation-only: Buddy remains neutralized and helpful, never attacks, sabotages, invents sensor evidence, bypasses host authority or encourages a lethal decision. Set `[Character] SlowBurnHorror = false` for the ordinary coworker throughout.
- Adds the one-shot `[Character] ResetSlowBurnProgress` switch to restart the current save's story without deleting the campaign.
- Expands deterministic release checks from 70 to 91.

## 2.4.3

- Fails closed when Steam lobby visibility is missing, unknown or throws: remote PTT and remote state-changing game actions require a verified friends/invite-only lobby unless the host explicitly opts in.
- Makes raw response journaling an explicit opt-in (`[Logging] SaveResponses = false`) and migrates older configs to the private default.
- Correlates journal inputs and replies by turn ID, preventing deterministic replies, observations and Realtime tool results from pairing with another player's input.
- Removes arbitrary terminal passthrough and requires an explicit enable/disable/open/close verb for facility codes.
- Rejects overflowing RIFF chunk sizes, refuses recovery teleports without a nearby NavMesh point, and fixes OpenAI screenshot requests to use Responses image input with the configured Luna model.
- Replaces the oversized gameplay encyclopedia prompt with a lean v81 coworker policy: short grounded replies, evidence-only proactivity and precise host-authoritative action rules.
- Adds public-release privacy and AI-generated voice disclosures and expands deterministic release checks from 56 to 70.

## 2.4.2

- Public-lobby hardening: remote push-to-talk voice is rejected by default in public Steam lobbies, protecting the host's API budget and keeping strangers' audio away from the speech service. Friends/invite-only lobbies are unaffected; hosts can opt in with `[Security] RemoteVoiceInPublicLobbies = true`. The host gets an in-game notice when the lobby is public.

## 2.4.1

- Commands with real side effects (`buy`, `route`, `moons`, `store`, `credits`, terminal actions) now only run when Buddy is actually addressed, so plain chat like "buy 3 flashlights" or "route titan" can no longer spend credits or move the ship by itself. Pleaded spawn requests stay available without addressing and remain plea/item/cap sandboxed.
- Guards the enemy-kill patch against registry exceptions: a failure can no longer abort vanilla enemy deaths.
- Fixes the old config migration clobbering Groq users' STT/TTS models with OpenAI models; provider selections are preserved.
- Honors the `[Vision] Enabled` setting (still default-off); enabling it actually enables host screenshots for visual questions instead of being silently reset every launch.
- Clears the speech queue on lobby change so stale Buddy lines cannot play into the next session.
- Movement: Buddy now returns to the ship exterior when the owner is on the ship while he is outside, and recovery teleports are refused when no NavMesh exists near the target (no more void drops).
- Spawn fallback refuses to adopt Masked enemies that are alive, targeting a player, or mid-attack.
- Chat replies via Luna get a higher output ceiling so multi-sentence answers no longer truncate.
- Audio: the ChatHearRange positional setting works again (default remains global); the playback clip is released on stop; the client mic clip is destroyed when replaced.
- Voice validation now parses RIFF chunks properly (accepts INFO chunks, rejects non-PCM formats) and accepts "route to <moon>" phrasing.
- Name tags sanitize control characters; response journal callouts no longer consume pending chat pairings.
- 56 automated release checks (was 50).

## 2.4.0

- Makes command execution genuinely agentic: the system prompt now has an explicit command catalogue (stay/stay in place, follow, move/go forward, scout, fetch, ship, terminal, facility codes, polite spawn) and a hard execution contract. On voice Buddy must call `execute_game_command` for any command instead of talking about it; on text chat he may only acknowledge the confirmed result. Ambiguous phrases default to calling the tool, so commands can no longer be lost to conversation.
- Fixes `stay in place`, `stay put`, `stay right here`, `stand by` and `freeze` not being recognized as stay commands by the deterministic parser.
- Raises the native Realtime output token ceiling so a preamble, tool call and spoken reply fit without truncation, making voice commands more reliable.
- Stops Buddy performing joke demands (laugh, yell, pretend) as if they were commands: one dry refusal, then back to the job.
- Grounds scrap/monster answers strictly in the live sensor block: Buddy reports the real loose-scrap count within 25 m and names only entities the host actually sees.
- Adds a response journal: every Buddy reply (chat, voice, deterministic commands, danger callouts) is written with its paired player input to `BepInEx/LethalAICrewmate-responses.log`, so the exact exchanges can be reviewed. Disable with `[Logging] SaveResponses = false`.

## 2.3.0

- Completes the dry, practical coworker release: the stock personality setting and the OpenAI speech instructions now match Buddy's v2.2.3 prompt voice, and untouched installs still on the old goofy default migrate once (custom personality text is preserved).
- Adds a two-tier deterministic danger callout: a calm warning when a hostile creature gets within 12.5 m of any living player, then a shouted "RUN!" when it closes to 7.5 m, each with its own short cooldown.
- Tightens output budgets: chat replies cap at 64 tokens and native Realtime voice at 96, matching Buddy's short-reply contract and trimming API cost.

## 2.2.4

- Switches OpenAI speech transcription to the requested `gpt-live-transcribe` model for both native Realtime voice sessions and the non-native OpenAI STT path.
- Removes the old migration that silently converted `gpt-live-transcribe` back to `gpt-realtime-whisper`.

## 2.2.3

- Natural pleaded requests now work: "Can I please have a flashlight? I'm begging you" is treated as the same bounded, validated item-spawn request as "please spawn a flashlight".
- Prevents tool-turn preambles from playing before Buddy receives the real command result, so he no longer promises an item and then contradicts himself.
- Raises native Ash Realtime voice output slightly with a soft ceiling.
- Reworks Buddy's personality into dry, practical coworker humour and removes forced chaotic/internet-style jokes.

## 2.2.2

- Fixes a native Realtime cancellation race: pressing push-to-talk between completed responses no longer disconnects Buddy with "Cancellation failed: no active response found".
- Keeps Ash voice and makes spoken responses tighter: normally 3-12 words, led by danger, scrap, route, or the next useful crew action.

## 2.2.1

- Fixes OpenAI immediately rejecting native Realtime voice sessions by supplying the required 24 kHz output PCM rate.
- Uses Ash voice with `gpt-realtime-whisper`, far-field noise reduction, unlimited output tokens and low reasoning.
- Keeps turn detection explicit for Buddy's hold-to-talk controls and shows the actual API error in-game instead of the generic disconnected message.

## 2.2.0

- Rebuilds OpenAI push-to-talk as genuine `gpt-realtime-2.1-mini` speech-to-speech instead of a text-only Realtime request followed by separate TTS.
- Keeps one authenticated host WebSocket session, sends committed 24 kHz PCM turns, and starts playing native streamed PCM output in bounded chunks with the Ash voice.
- Gives host and remote players the same Realtime path; remote audio remains bounded, validated and host-authoritative.
- Adds native input transcripts, synchronized Buddy audio/chat, session reconnect handling and clean disconnect cancellation.
- Adds a Realtime function tool backed by the existing deterministic host command parser, so movement, scouting, scrap, purchases, ship/facility controls, status and polite spawning execute before Buddy reports success.
- Keeps Luna Fast mode for text chat while native voice uses the dedicated low-cost Realtime mini model.

## 2.1.0

- Moves Luna Responses requests to the OpenAI Fast service tier while keeping low reasoning and short replies.
- Switches stock OpenAI speech to `gpt-realtime-whisper` and `gpt-4o-mini-tts` with the natural-speed `cedar` voice.
- Makes Buddy's writing and voice performance more expressive, reactive and situationally funny without lengthening routine answers.
- Fixes remote-player replies being silently lost while Buddy was already speaking by queueing a small number of speech lines in order.
- Refocuses Buddy on safe scrap recovery, enemy avoidance, exits and concrete next actions instead of unrelated trivia or generic chatter.
- Fixes ordinary real-world weather questions being mistaken for the current moon-weather status command.
- Makes polite item spawning accept natural articles and common plurals while preserving host authority, prefab validation and hard quantity limits.

## 2.0.0

- Promotes the OpenAI-first release to v2 with Luna low reasoning, low verbosity, a purpose-built coworker prompt and deterministic host-side commands.
- Adds a deliberately silly but bounded polite item spawner: a player must explicitly say please or beg, only validated grabbable game items can spawn, quantities are capped at 3 and each round is capped at 12.
- Places spawned items in front of the requesting host or friend using server network authority; enemies, arbitrary prefabs, hazards and unknown names are rejected.
- Grounds the prompt in Version 80 official notes, current wiki mechanics and practical community guidance, including the new creatures and changed moon assumptions.
- Documents the exact movement, terminal, ship, facility and spawn contracts with strict untrusted-input and prompt-injection boundaries.

## 1.8.0

- Rebuilds Buddy's system prompt as a compact coworker policy with verbatim command interpretation, complete real capability mapping, multiplayer awareness, strict result grounding and prompt-injection resistance.
- Enables Luna's lowest non-off reasoning level (`low`) and low response verbosity while retaining the cheap Luna / mini-transcribe / TTS-1 stack.
- Adds deterministic recognition for `stay still`, `stand still`, `do not move`, `move forwards` and `walk forwards`.
- Keeps all game-changing actions host-authoritative: the model cannot directly move Buddy, spend credits or control hazards, and generated action tags remain inert.
- Removes the large always-sent gameplay encyclopedia from the system prompt to reduce request size and latency.

## 1.7.2

- Doubles retained conversation history from three to roughly six complete player/Buddy exchanges while keeping the short 96-token response cap.

## 1.7.1

- Changes untouched OpenAI installs from neutral `alloy` to the lighter male `echo` voice and slightly quickens playback.
- Recasts Buddy as a goofy, useful male coworker with short situational banter instead of formal assistant phrasing.
- Caps normal replies more aggressively and trims retained chat history for faster Luna turns.
- Preserves customized personality and voice settings during migration.

## 1.7.0

- Removes the experimental Realtime WebSocket brain and migrates stock installs to the fast cost-focused OpenAI stack: `gpt-5.6-luna` through Responses with no reasoning delay, `gpt-4o-mini-transcribe`, and speed-optimized `tts-1`.
- Keeps every gameplay action deterministic and host-authoritative rather than model-controlled: movement, scouting, scrap fetching, purchases, routing, status, ship doors/lights, and facility doors/turrets/mines.
- Expands natural scouting phrases including `scout forwards`, `check the next room`, `push ahead`, and `clear the way`, while retaining bounded pathing, a real scout report, and return-to-follow behavior.
- Tightens the system prompt around Buddy's actual capabilities, truthful tool results, short voice-chat replies, multiplayer awareness, and the text-only screen boundary.
- API keys saved from the menu persist securely in Windows Credential Manager.

## 1.6.5

- API keys saved from Buddy's main-menu panel now survive game restarts in the host's Windows Credential Manager instead of being written as plaintext to the BepInEx config.
- Environment variables still override a saved key, and Clear removes the saved credential and the legacy plaintext config entry for the selected provider.

## 1.6.4

- Replaces the rejected REST calls for `gpt-realtime-2.1-mini` with the model's native authenticated Realtime WebSocket endpoint.
- Sends Buddy's full system prompt and bounded conversation context through a text-only `response.create`, collects streamed `response.output_text` events, and feeds the result into the existing multiplayer chat and synchronized TTS pipeline.
- Remains a strict `gpt-realtime-2.1-mini` build with no Luna or other chat-model fallback.

## 1.6.3

- Fixes Buddy ignoring voice and chat with `gpt-realtime-2.1-mini`: OpenAI rejects this model on Chat Completions, so the experimental model now uses the Responses API and parses `output_text` replies.
- Keeps this as a strict `gpt-realtime-2.1-mini` test with no fallback to another chat model; failures are logged directly.

## 1.6.2

- Fixes multiplayer handshakes after leaving and creating or joining another lobby without restarting Lethal Company. Netcode clears named handlers on shutdown, so Buddy now registers them again for every listening session.
- Extends the initial handshake grace period and no longer falsely claims a player lacks the ZIP when the host simply received no handshake.
- Logs every client hello send so failed multiplayer setup is straightforward to diagnose.

## 1.6.1

- Switches the stock OpenAI chat model to `gpt-realtime-2.1-mini` for the first quality and latency test.
- Keeps `gpt-4o-mini-transcribe` input and `tts-1` WAV output so the existing host-authoritative multiplayer voice relay stays unchanged during this test.

## 1.6.0

- Adds a provider-aware OpenAI path for text chat, host/friend transcription, speech generation, key testing and host-only secret handling while retaining optional Groq compatibility.
- Uses the cost-focused OpenAI stack: `gpt-5.6-luna`, `gpt-4o-mini-transcribe`, and realtime-optimized `tts-1` with WAV output for Buddy's existing synchronized playback.
- Adds `LETHAL_AI_OPENAI_API_KEY` as the preferred persistent key source and supports session-only OpenAI keys from the main-menu panel.
- Sends Luna a Chat Completions-compatible reasoning payload with `reasoning_effort=none`, and keeps screenshots disabled.
- Expands release secret scanning to reject both Groq and OpenAI-shaped API keys.

## 1.5.3

- Switches Buddy's stock text model to Groq `openai/gpt-oss-120b` for stronger conversation, instruction following and reasoning.
- Disables host screenshot capture entirely, including for older configs that previously enabled vision, making v1.5.3 text-only.
- Migrates existing installs that still use the previous stock Qwen chat model to GPT-OSS 120B once, while preserving other custom model choices.

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
