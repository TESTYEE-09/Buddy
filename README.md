# LethalAICrewmate

Buddy is an AI crewmate for **Lethal Company v81**. He spawns with the crew, follows players, responds in chat, can take simple orders, fetch scrap, and can use Groq for chat, speech-to-text and TTS.

## Install

1. Install **BepInExPack 5.4.2100** for Lethal Company.
2. Put `LethalAICrewmate.dll` in `BepInEx/plugins/LethalAICrewmate/`.
3. Launch the game.
4. On the main menu, paste a Groq API key into the small **Lethal AI Crewmate — Groq** box and press **Save**.
5. Host a lobby and land on a moon. Buddy is spawned and controlled by the host.

Get a Groq key from the Groq console. The key is saved to your local BepInEx config and is not sent to other players.

## Multiplayer

**All players should install the same LethalAICrewmate version.** The mod uses a normal Lethal Company Masked network object for Buddy, but client-side patches are also required to identify him as friendly and render synced mod behavior correctly.

- The **host is authoritative** for Buddy movement, commands, AI requests and item actions.
- Only the **host needs a Groq API key**.
- Clients register the multiplayer handlers automatically when networking starts.
- Clients handshake with the host using a small protocol/version check.
- Late joiners request the current Buddy network ID and held-item state, so joining after Buddy spawned is supported.
- Host state messages are accepted only from the server; clients cannot directly spoof Buddy state through the mod's custom-message handlers.
- Buddy chat text is synced to modded clients. Groq TTS playback is currently generated on the host.

If the host and client mod versions do not match, the client logs a compatibility warning and the main-menu panel can show the mismatch after returning to the menu.

## Commands

Type commands in normal Lethal Company chat:

| Command | What Buddy does |
| --- | --- |
| `buddy follow` | Follows a living player |
| `buddy stay` | Holds his current position |
| `buddy go to ship` | Returns toward the ship |
| `buddy fetch scrap` | Finds nearby scrap and brings it back |

You can also address Buddy normally in chat or ask a question near him.

## Voice

The host can hold **V** by default to talk to Buddy.

`host microphone -> Groq Whisper -> Buddy response -> Groq Orpheus TTS`

Voice options are in `BepInEx/config/com.lethalaicrewmate.buddy.cfg`.

## Main config

```ini
[Groq]
ApiKey =
Model = qwen/qwen3.6-27b
SttModel = whisper-large-v3-turbo
TtsModel = canopylabs/orpheus-v1-english
TtsVoice = troy
TtsEnabled = true
TtsDirection = nervous

[Crewmate]
Name = Buddy
Enabled = true
ChatHearRange = 25
ChatTriggerRange = 25
ObservationIntervalSeconds = 0

[Voice]
Enabled = true
PushToTalkKey = V
MaxRecordSeconds = 8

[Vision]
Enabled = true
```

## Building

The project targets `netstandard2.1` and references Lethal Company v81 game libraries through NuGet.

```bash
dotnet restore src/LethalAICrewmate.csproj
dotnet build src/LethalAICrewmate.csproj -c Release
```

The repository build workflow copies the Release DLL into the Thunderstore package and produces a ready-to-install ZIP artifact.

## Package contents

A release ZIP contains:

- `LethalAICrewmate.dll`
- `manifest.json`
- `README.md`
- `CHANGELOG.md`
- `icon.png`

## Notes

- Do not publish a Groq key in the repo or inside the DLL.
- If an older build had a shared/default key, rotate that key before distributing the mod.
- Multiplayer support assumes every player uses the same mod version.
