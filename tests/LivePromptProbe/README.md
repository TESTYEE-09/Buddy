# Live prompt probe

Runs Buddy's **real** system prompt and **real** tool schemas against the live Realtime model and
grades what comes back. Everything else in `tests/` reads source text; this is the only thing that
checks what the model actually does with it.

It exists because three bugs got through code review and static checks, and only turned up here:

- Buddy reciting his own per-turn briefing as dialogue.
- Polite requests ("can you grab that?") being treated as small talk by the contract while the
  model correctly read them as orders.
- The preamble — the model speaking *before* it calls a tool, which is the "he answers twice" bug.
  It still happens on roughly one tool turn in eight. The prompt does not stop it. The mod discards
  it (see `ACTING AND SPEAKING ARE SEPARATE` in the contract and the audio buffering in
  `OpenAiRealtimeVoiceClient.ProcessTurnAsync`). **The probe reports it separately for that reason:
  a preamble is not a failure of the mod, it is proof the containment is still needed.**

## Running it

```bash
python tests/LivePromptProbe/extract.py     # pulls contract + tool schemas out of src/
OPENAI_API_KEY=sk-... node tests/LivePromptProbe/probe.mjs
```

Requires Node 18+ (uses the global `WebSocket`) and Python 3. No packages to install.

Never hardcode the key. Pass it in the environment, and never commit it — this repo is public.

## Cost and rate limits

Output is text-only, which is what keeps it cheap: audio tokens dominate otherwise. A full 19
scenario run is roughly 120k input tokens (much of it cached) and 1.5k output — cents, not dollars.

The account limit that bites is **tokens per minute**, not spend. At 40k TPM a full run trips
`rate_limit_exceeded` about halfway through and the affected responses come back with
`status: "failed"` and **empty text**, which looks exactly like the model refusing to answer. That
is why the probe prints `status=` on every failure and sleeps 8s between scenarios. If you see a
cluster of empty replies, check the status before believing the model did anything wrong.

## Reading the output

`ok` / `FAIL` per scenario, then a summary. A `PREAMBLE:` note is informational. Real failures are
a tool called on conversation, a tool missed on an order, a reply that parrots the status wording,
or forbidden vocabulary reaching speech.

Last full run on 5.1.1: 19/19 behaviour, preamble on 1 of 8 tool turns.
