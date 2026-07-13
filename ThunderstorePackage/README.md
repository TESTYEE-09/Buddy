# LethalAICrewmate

AI crewmate NPC (**Buddy**) for **Lethal Company v81**. Follows, stays, returns to ship, fetches scrap. **Llama 4 Scout** for chat, **Whisper** for listening, **Orpheus** for speaking.

## Install

1. BepInExPack + this mod (r2modman / Gale / drop DLL in `BepInEx/plugins/`).
2. **Host must install** the mod. Clients should too (chat, attach, hostility suppress).
3. Free key: [console.groq.com/keys](https://console.groq.com/keys)
4. Config `BepInEx/config/com.lethalaicrewmate.buddy.cfg`:

```ini
[Groq]
ApiKey = gsk_your_key_here
Model = meta-llama/llama-4-scout-17b-16e-instruct
SttModel = whisper-large-v3-turbo
TtsModel = canopylabs/orpheus-v1-english
TtsVoice = troy
TtsEnabled = true
TtsDirection = nervous
```

## Why Llama 4 (not Qwen 3.6)

| | Llama 4 Scout | Qwen 3.6 27B |
|--|---------------|--------------|
| Fit for Buddy | Short in-character lines | Deep reasoning |
| Speed / free TPM | Strong free-tier limits | Heavier |
| Pick | **Default** | Set `Model = qwen/qwen3.6-27b` if you want |

## Commands

| Chat | Effect |
|------|--------|
| `buddy follow` | Follow you |
| `buddy stay` | Hold position |
| `buddy go to ship` | Path to ship |
| `buddy fetch scrap` | Deliver nearest scrap |

## Voice pipeline

```
You hold V → Whisper STT → Llama 4 reply → Orpheus TTS (spoken near Buddy)
```

- **STT:** hold `Voice.PushToTalkKey` (default **V**), speak, release.
- **TTS:** `canopylabs/orpheus-v1-english`, max **200 chars** per line. Host hears **3D** audio at Buddy.
- Voices: `troy` `austin` `daniel` (M) · `autumn` `diana` `hannah` (F).

## Source

https://github.com/TESTYEE-09/LethalAICrewmate
