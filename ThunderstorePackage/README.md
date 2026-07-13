# LethalAICrewmate

AI crewmate NPC (**Buddy**) for **Lethal Company v81**. Host spawns a neutralized Masked body that follows, stays, returns to ship, fetches scrap, chats via **Groq**, and listens via **Groq Whisper** push-to-talk.

## Install

1. BepInExPack + this mod (r2modman / Gale / manual DLL into `BepInEx/plugins/`).
2. **Host must install** the mod. Clients should too (chat, attach visuals, hostility suppress).
3. Get a free key: [console.groq.com/keys](https://console.groq.com/keys)
4. Set in `BepInEx/config/com.lethalaicrewmate.buddy.cfg`:

```ini
[Groq]
ApiKey = gsk_your_key_here
Model = llama-3.1-8b-instant
SttModel = whisper-large-v3-turbo
```

## Commands (text chat)

| Command | Effect |
|---------|--------|
| `buddy follow` | Follow you |
| `buddy stay` | Hold position |
| `buddy go to ship` | Path to ship |
| `buddy fetch scrap` | Deliver nearest scrap |

## Voice (host)

**You → Buddy (STT):** Hold **V**, speak, release → Whisper transcript → Buddy.

**Buddy → You (TTS):** After each LLM reply, Orpheus speaks near Buddy in 3D (host only).  
Voices: `troy` `austin` `daniel` (M) / `autumn` `diana` `hannah` (F).  
Orpheus max **200 characters** per line (mod truncates + asks for short replies).

Disable mic: `Voice.Enabled = false`. Disable speech: `Groq.TtsEnabled = false`.

## Spawn

Buddy spawns after you **land on a moon** (host). Spawns near you. Check `BepInEx/LogOutput.log` for:

- `Crewmate spawn requested`
- `spawned successfully`

If spawn fails, the log lists available `EnemyType` names.

## Config highlights

| Key | Default | Notes |
|-----|---------|--------|
| Groq.ApiKey | empty | Silent NPC without key; commands still work |
| Groq.Model | llama-3.1-8b-instant | Fast free-tier chat |
| Groq.SttModel | whisper-large-v3-turbo | Free STT |
| Voice.PushToTalkKey | V | Hold to talk |
| Crewmate.Enabled | true | Master spawn toggle |

## Source

https://github.com/TESTYEE-09/LethalAICrewmate
