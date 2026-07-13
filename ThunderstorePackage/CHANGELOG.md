# Changelog

## 1.1.0

- **Spawn fix:** poll while landed + OpenShipDoors hook + Instantiate fallback; spawn near player; verbose diagnostics.
- **Groq** replaces OpenRouter for chat (fast). Default model `llama-3.1-8b-instant`.
- **Voice (STT):** hold `V` (configurable) → Groq Whisper `whisper-large-v3-turbo` → Buddy commands/chat.
- Legacy `OpenRouter.ApiKey` auto-migrates into `Groq.ApiKey` if set.

## 1.0.1

- Client registry sync; spawn retries; extra hostility guards; scrap double-count guard; LLM session reset.

## 1.0.0

- Initial release for Lethal Company v81.
