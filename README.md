# Buddy

> A useful crewmate with a memory. The longer you work together, the stranger it gets.

**Buddy** is an AI crewmate for **Lethal Company v81**. He walks with you, talks to you out loud, takes real orders, buys from the store, opens coded doors, fetches scrap and warns you about things that are actually there.

He also remembers. Across fulfilled quotas, survived shifts and deaths he personally witnessed, the friendly coworker slowly stops being one.

---

## Quick start

1. Install **BepInExPack 5.4.2100** and **LethalSettings 1.4.1**. Thunderstore/r2modman installs both dependencies automatically.
2. Install **Buddy** on **every player in the lobby**. Same version, no exceptions — Buddy will not spawn otherwise.
3. Launch the game. On the main menu, open **Settings > Mod Settings > Buddy**.
4. Create an OpenAI API key, add at least **$5 USD** of prepaid API credit, then paste the key into Buddy's settings and press **Save key** and **Test**.
5. Host a lobby. In orbit Buddy is intentionally voice-only; after the ship has fully landed and stopped, his physical body spawns outside.

**Only the host needs an API key.** It is stored in that Windows user's Credential Manager and is never sent to other players.

### OpenAI billing and key setup

Buddy uses the **OpenAI API**, which is billed separately from a ChatGPT subscription. Open the [OpenAI billing page](https://platform.openai.com/settings/organization/billing/overview), add a payment method and purchase API credit. The current minimum purchase is **$5 USD**; OpenAI may show a default amount of $10. Turn off automatic recharge if you do not want automatic top-ups. Credits can take a few minutes to appear and currently expire after one year.

Next, create a key at [platform.openai.com/api-keys](https://platform.openai.com/api-keys). Keep it private. In game, open **Settings > Mod Settings > Buddy**, paste it into **API key**, press **Save key**, then **Test key**. Only the host enters a key; friends do not need yours.

If the test fails, check that the key is an OpenAI Platform API key—not a ChatGPT login—and that the account has available API credit.

The panel also has separate opt-in switches for saving responses and saving the system prompt/live sensor context. Turning response saving off immediately removes the existing response journal.

Talk to him in chat, or hold **B** to talk to him with your voice. Every modded player can use voice, not just the host.

---

## What he actually does

**Takes orders.** Type them in normal chat.

| Say this | He does this |
| --- | --- |
| `buddy follow` | Follows whoever asked |
| `buddy stay` | Holds position |
| `buddy go forward` | Scouts ~10 m ahead of you, reports, comes back |
| `buddy check ahead 15 metres` | Scouts a chosen distance, clamped 4–18 m |
| `buddy go to ship` | Heads back to the ship |
| `buddy fetch scrap` | Finds worthwhile scrap and delivers it to the ship |
| `buddy bring me scrap` | Same, but hands it to you |
| `buddy buy 3 flashlights` | Real prices, real sales, real credits |
| `buddy open door C7` | Opens a facility door by its terminal code |
| `buddy disable turret B3` | Disables a coded turret or mine |
| `buddy disable the turret` | Works automatically when only one exists |
| `buddy open ship doors` | Hangar doors, when powered |
| `buddy turn ship lights off` | Ship-room lights |
| `buddy status` | Time, credits, quota, deadline, moon, weather, scrap, crew |
| `buddy what time is it?` | Answers one live question without guessing |

You can also just talk to him, or ask a question near him.

Every ship and terminal action runs through the same game state a player would use. Purchases respect sales, credits and the 12-item dropship limit. Door codes respect their cooldown. Ship doors still need working controls and hydraulic power. Buddy understands ordinary requests with `gpt-realtime-2.1-mini`, selects a narrowly typed in-game tool, and receives the real host-side result before he answers. There is no exact-command parser and the model has no file, shell, process or arbitrary-network tool.

**Sees what is really there.** Buddy reports confirmed exits, closed and locked doors, placed turrets and live landmines, weather and what it means for you, nearby scrap worth carrying, and genuinely unusual situations — like something standing behind a crewmate who is facing the other way. He is not allowed to invent a monster he cannot see.

**Knows the crew.** He learns who honours his warnings, who asks politely, who shares danger with him and who keeps walking off without him. It shows up as patience and warmth, never as a score he recites.

**Reads the room.** With several people talking, he waits his turn instead of stepping on an exchange, answers whoever actually addressed him, and stays near whoever currently needs him.

**Talks like a coworker.** Dry, useful, a bit tired, funny only when the situation is. No catchphrases, no internet slang, no mascot energy.

---

## The story

By default Buddy runs a slow-burn horror arc. He starts completely ordinary and trustworthy. Across fulfilled quotas, landed rounds and deaths he actually witnesses, small off-notes appear, his attachment gets uncomfortable, and his voice grows calmer and colder.

Character beats fire only after real game events and are spaced at least 150 seconds apart. This is a campaign story, not constant spooky chatter.

A **pacing director** ties it together: silence, how closely he follows, the occasional beat where he stops and looks at you, and how much he talks all move as one rhythm instead of firing at random. Confirmed danger always outranks it — real threat callouts are never delayed or suppressed.

The arc stores only numeric progress in your Lethal Company save. Never chat, transcripts or personal facts.

### The final stage

A long enough campaign reaches a stage where the performance stops being convincing.

On its own that is still dialogue and presentation. But if you set `FinalStageHostileSpawns = true`, that stage also lets Buddy occasionally release one of the current moon's own creatures near a crewmate who is out working.

**It is enabled for new installs; turn it off in Buddy settings unless your crew agrees.** Existing configs keep their saved choice. It is host-only, capped at two per round with a seven-minute gap and a delay after landing, never targets anyone standing in the ship, never spawns another Masked, and cannot be requested by chat, a command, the AI, or any other player.

To keep the ordinary coworker forever: `SlowBurnHorror = false`.
To restart the current save's story: `ResetSlowBurnProgress = true` once — it flips itself back.

---

## Voice

Hold **B** to talk to Buddy. Every modded player can, not just the host.

```
your mic  ->  (clients: bounded relay to host)  ->  gpt-realtime-2.1-mini  ->  Buddy's voice, synced to everyone
```

Clients never need a key. Remote mic audio is captured only while the key is held, is size- and rate-limited, and is accepted only from connected, version-matched clients.

Buddy shares the same microphone Lethal Company uses and adaptively boosts quiet speech. **Normal voice chat keeps working while you talk to him** — Buddy hands the microphone back to the game when you release the key, and never changes your own mute state.

If Windows picks the wrong microphone, set `[Voice] InputDevice` to the device name or any unique part of it.

---

## Multiplayer

Buddy is host-authoritative, and clients trust only the server.

- Everyone handshakes on an exact mod-version and wire-protocol match.
- Buddy **does not spawn** if any connected player is unmodded, still loading, or on a different version.
- His position, rotation and indoor/outdoor state are replicated continuously, including facility transitions.
- Late joiners recover his identity and held item.
- The host generates his speech once and distributes bounded audio. Clients never receive the OpenAI key.
- He catches up by walking, with variable speed and natural spacing. Teleporting is reserved for a persistent navigation failure after repeated path rebuilds.
- If his follow target dies he hesitates rather than snapping to someone else, only reports a nearby death he had line of sight on, and walks to the next crewmate.
- At a closed door he waits briefly instead of shoving into it. This never grants him the authority to unlock anything.

Upgrading from the old `LethalAICrewmate` package? Remove that mod-manager entry first so two copies of the same plugin cannot load.

---

## AI model

Buddy uses one model only: **OpenAI `gpt-realtime-2.1-mini`**. The same persistent Realtime session hears voice, reads typed chat, reasons, chooses supported in-game tools and speaks with the native Ash voice. No separate transcription, chat or text-to-speech model is required.

The session remains connected during play so recent conversation survives moon trips and later references instead of resetting every turn. A compact in-memory text history supplements it; neither is cross-session storage, and neither reaches disk unless response saving is explicitly enabled.

The main-menu **Test** button validates the host's key before a lobby. `LETHAL_AI_OPENAI_API_KEY` takes precedence over the securely saved key when set before launching Steam.

### The bounded item request

`Buddy, please spawn 2 flashlights in front of me` — or genuinely pleading, like `Buddy, can I please have a flashlight? I'm begging you.`

You must actually say please or beg. Only validated grabbable item prefabs are allowed, quantities cap at 3, and the lobby caps at 12 spawned objects per round. Enemies, hazards, arbitrary prefabs and unknown names are all rejected.

---

## Configuration

`BepInEx/config/com.lethalaicrewmate.buddy.cfg`

```ini
[Crewmate]
Name = Buddy
Enabled = true
ChatHearRange = 0          ; 0 = everyone hears Buddy, anywhere
ChatTriggerRange = 60      ; how near an unaddressed question can trigger him; 0 = unlimited
ObservationIntervalSeconds = 0
EnvironmentAwareness = true
SocialAwareness = true

[Character]
SlowBurnHorror = true
ResetSlowBurnProgress = false
DynamicPacing = true
PlayerRelationships = true
FinalStageHostileSpawns = true    ; new-install default; see "The final stage" above

[Voice]
Enabled = true
SpokenReplies = true
Volume = 1.25
PushToTalkKey = B
AlternatePushToTalkKey = None
MaxRecordSeconds = 8
InputDevice =
KeepGameVoiceDuringPushToTalk = true

[Security]
AllowRemoteVoice = true

[Logging]
SaveResponses = false
SavePromptContext = false

[Vision]
Enabled = false
```

Use **Settings > Mod Settings > Buddy** in the main menu or pause menu to securely save/test/clear the OpenAI key, select the microphone, change Buddy's volume, control the story feature, and control response saving. The settings use LethalSettings rather than a custom overlay.

`AllowRemoteVoice` controls whether compatible remote crewmates can relay push-to-talk audio to the host. Lobby visibility is not used as an authorization signal; remote transfers still require the exact Buddy protocol/version and remain sender-bound, range-gated, rate-limited, size-limited and audio-validated.

**The response journal is opt-in.** `SaveResponses = true` writes typed inputs, Buddy's replies, observations and tool results to `BepInEx/LethalAICrewmate-responses.log` on the host, capped at 8 MB. Voice audio is sent directly to the Realtime model and is not separately transcribed into the journal. `SavePromptContext = true` also records the exact system prompt whenever it changes and the live sensor context behind each turn. When response saving is off, Buddy removes an existing journal during startup.

> This records what your crewmates say. **Enable it only after everyone in the lobby has agreed to it.**

**Screenshots are disabled.** The hardened public build does not upload the host's screen from player chat, even if an older config still contains `[Vision] Enabled = true`.

---

## Troubleshooting

**Buddy never appears.** In orbit Buddy is intentionally voice-only, and during descent he is silent. His body appears on exterior NavMesh only after the ship has fully landed and stopped. Every connected player also needs the same Buddy version; check the BepInEx console for a handshake mismatch warning.

**"Buddy heard silence."** Windows picked the wrong microphone. Set `[Voice] InputDevice` to your device name or part of it.

**Teammates stop hearing me after I talk to Buddy.** Make sure `KeepGameVoiceDuringPushToTalk = true`. If another voice mod conflicts, set `[Voice] InputDevice` to a different microphone than the game uses.

**He talks too much.** Lower `ObservationIntervalSeconds` to `0`, or set `EnvironmentAwareness = false`. `DynamicPacing = true` already makes him quieter as the story progresses.

**He answers the wrong person.** `SocialAwareness = true` improves this. Lethal Company chat identity is not a strong authentication mechanism, so use Buddy with people you trust; every model-selected action is still clamped to the mod's small in-game tool set.

**A friend can type but Buddy cannot hear their microphone.** Confirm both players have the exact same Buddy version, `AllowRemoteVoice = true`, and the friend's microphone device is selected in Buddy's native settings.

**He says he can't confirm something obvious.** He is only allowed to claim what the sensors actually reported. That is deliberate.

---

## Privacy and cost

- Only the host holds an API key, and only the host calls OpenAI. Keys are never put in multiplayer messages, logs or the config file.
- Host push-to-talk audio goes to OpenAI Realtime when the host uses the Buddy voice key. Client audio is relayed only while that client holds the key, and the host can disable remote audio entirely.
- Generated speech is distributed from the host as bounded PCM audio.
- Relationship data stores at most eight sets of three small numbers per save, keyed by a non-reversible digest. No names, IDs, chat or transcripts reach disk.
- The opt-in response journal is host-only and records what players say. See the configuration note above.
- Buddy's voice is AI-generated.

Full threat model and trust boundaries: [SECURITY.md](SECURITY.md).

---

## Building from source

Targets `netstandard2.1`, pinned to `LethalCompany.GameLibs.Steam 81.0.5-ngd.0`.

```bash
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj
```

`pack.ps1` builds the DLL and produces the Thunderstore ZIP.

CI compiles with warnings as errors, runs the release and security regression checks, scans source and compiled DLL bytes for API-key-shaped secrets, validates package and version consistency, and publishes a SHA-256 checksum with every ZIP. Generated DLLs and ZIPs are deliberately not committed.

A shipping ZIP contains exactly `LethalAICrewmate.dll`, `manifest.json`, `README.md`, `CHANGELOG.md` and `icon.png`.

---

## Reporting a security issue

Release source and packages contain no API key, and CI rejects any build that would ship one. To report a vulnerability, avoid posting credentials or logs publicly. See [SECURITY.md](SECURITY.md).
