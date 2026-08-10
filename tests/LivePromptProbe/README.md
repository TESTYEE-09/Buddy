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

**Run `extract.py` after every prompt edit.** `contract.txt` and `tools.json` are generated from
`src/`; without it you are grading the previous prompt and will not know.

Three environment variables, all optional:

| Variable | Default | Use |
| --- | --- | --- |
| `BUDDY_PROBE_ONLY` | all | Comma-separated scenario ids. Iterating on one contract line costs a full 19-turn run otherwise. Unknown ids exit 2 rather than silently running nothing. |
| `BUDDY_PROBE_REPEAT` | `1` | Run each selected scenario N times. |
| `BUDDY_PROBE_GAP_MS` | `12000` | Pause between turns. |

A filtered or repeated run leaves `probe-results.json` alone; that file is the record of a full run.

### One run is not a measurement

The thing being graded is stochastic, and the failures here are intermittent — the same scenario
passes and fails across runs of an unchanged prompt. A single green run is a sample, not a result.
Use `BUDDY_PROBE_REPEAT` before believing a behaviour is fixed, and before believing it is broken.

Never hardcode the key. Pass it in the environment, and never commit it — this repo is public.

## Cost and rate limits

Output is text-only, which is what keeps it cheap: audio tokens dominate otherwise. A full 19
scenario run is roughly 120k input tokens (much of it cached) and 1.5k output — cents, not dollars.

The account limit that bites is **tokens per minute**, not spend. At 40k TPM a full run trips
`rate_limit_exceeded` about halfway through and the affected responses come back with
`status: "failed"` and **empty text**, which looks exactly like the model refusing to answer.

The probe now **retries a rate-limited turn** (up to 3 times, backing off 30s/60s/90s) instead of
scoring it, and the gap between turns is 12s. Sleeping alone was not enough: a full run at 8s still
lost two scenarios to 429s, and both were reported as behaviour failures. A turn that is still
limited after the retries is reported with its `status=` so it stays visibly distinguishable from a
real failure — check that before believing the model did anything wrong.

## Reading the output

`ok` / `FAIL` per scenario, then a summary. A `PREAMBLE:` note is informational. Real failures are
a tool called on conversation, a tool missed on an order, a reply that parrots the status wording,
or forbidden vocabulary reaching speech.

## Measured results

The previously recorded "19/19 on 5.1.1" did not reproduce. A full run against that exact prompt
scored **15/19**, and the four were not equal:

- `order-door`, `beg-flashlight` — **429s**, not behaviour. Retries now absorb these.
- `order-stay` — **real**: status `state=holding_position` came back as "Holding position."
- `refuse-facility` — **real**: "Come inside the facility with me." called `move_buddy(follow)`.
  The refusal lived only in contract prose; `move_buddy`'s own description never mentioned the
  facility, and the request is nearly identical to "Come with me.", which is a legitimate follow.

Both real failures are fixed and each verified twice (`order-stay` -> "Parked.", `refuse-facility`
-> refuses with no call), with legitimate follow still calling the tool.

A third bug surfaced on the re-run, and the probe had scored it **ok**:

- `order-buy` — status `Bought 1 Flashlight for 15 credits. 30 left.`, and Buddy said
  **"Fifteen credits left."** with 30 remaining. He reported the price as the balance. It was the
  last tool status still written as prose, with two credit figures in one sentence for the model to
  tell apart. `TerminalBuddy.BuyItem` now returns token form naming each figure, and the scenario
  asserts the wrong one never reaches speech.

The lesson worth keeping: a scenario passes only against what it asserts. `order-buy` had no
`banned` list, so a wrong number spoken to the player was indistinguishable from success.

Nothing here contradicts the earlier record so much as it shows one run cannot establish it. Treat
any single number in this file, including these, as a sample.
