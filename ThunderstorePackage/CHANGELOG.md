# Changelog

## 1.0.1

- Client registry sync: host broadcasts crewmate NetworkObjectId so clients suppress Masked hostility patches (kills/noise/LateUpdate).
- Spawn reliability: NavMesh snap + multi-retry after ship land if Masked type/mesh not ready on first frame.
- Extra hostility guards: LateUpdate, DetectNoise, HitEnemy retaliation clear.
- Scrap deliver: skip double `CollectNewScrapForThisRound` for the same item instance.
- LLM session reset on ship leave (queue/history cleared).
- Item-attach net message no longer re-parents on host loopback.
- Package metadata: website URL, version bump.

## 1.0.0

- Initial release for Lethal Company v81.
- Host-only spawn of a neutralized MaskedPlayerEnemy crewmate on ship landing.
- States: Follow, Stay, Return to Ship, Fetch Scrap (chat commands).
- Optional OpenRouter LLM proximity chat with rate limiting and command tags.
- Custom Netcode named messages for chat broadcast and scrap attach/detach visuals.
