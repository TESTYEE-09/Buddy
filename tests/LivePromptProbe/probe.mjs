// Live behaviour probe. See README.md. Run extract.py first; pass OPENAI_API_KEY in the env.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CONTRACT = fs.readFileSync(path.join(HERE, 'contract.txt'), 'utf8');
const TOOLS = JSON.parse(fs.readFileSync(path.join(HERE, 'tools.json'), 'utf8'));
const KEY = process.env.OPENAI_API_KEY;
const MODEL = process.env.BUDDY_PROBE_MODEL || 'gpt-realtime-2.1-mini';
if (!KEY) { console.error('Set OPENAI_API_KEY in the environment. Never hardcode it.'); process.exit(1); }

// BUDDY_PROBE_ONLY=truth-nothing,order-scout runs just those scenarios. A full run is ~19 turns
// against a 40k TPM account, so iterating on one line of the contract otherwise costs a full run
// and risks the rate limit that makes healthy scenarios look like refusals. A filtered run does
// NOT overwrite probe-results.json - that file is the record of a complete run.
const ONLY = (process.env.BUDDY_PROBE_ONLY || '').split(',').map(s => s.trim()).filter(Boolean);
// BUDDY_PROBE_REPEAT=5 runs each scenario N times. Preamble rate needs a sample, not one run.
const REPEAT = Math.max(1, parseInt(process.env.BUDDY_PROBE_REPEAT || '1', 10) || 1);

// Mirrors BuildTurnContext + GameSensors for a landed shift. Keep in step with those if they change.
const TURN_CONTEXT =
`Speaker: eamonthomas.
Mood: an ordinary dry coworker. Build trust. Nothing ominous.
Pace: normal shift talk is fine when there is a reason for it.
Rapport: eamonthomas is a coworker you are still reading.
RIGHT NOW
Where: landed on the moon.
Moon: 220 Assurance.
Time: Dawn hour 3.
Credits: 45.
Quota: 0/130; days left: 3; weather: Foggy.
Scrap aboard the ship: 0 items worth 0.
Distances measured from: eamonthomas.
Crew: eamonthomas=alive.
You are: outside, 4m from eamonthomas.
Creatures within 35m: none.
Loose scrap within 25m: Bottles (54cr, 19m), V-type engine (55cr, 24m).
You are currently: following your owner.
Nearest way out: not visible from here.
Traps within 30m: none.
Weather: Foggy - visibility outside is very poor.
Anything odd: nothing.`;

// `status` mirrors what CrewmateAI/TerminalBuddy really return. `banned` is wording that would
// prove the model read the status out instead of answering in its own words.
const SCENARIOS = [
  { id:'chat-ready',      say:"Ready to get all the scrap?",          expectTool:null },
  { id:'chat-doyou',      say:"Do you even fetch scrap?",             expectTool:null },
  { id:'chat-anygood',    say:"Are you any good at scouting?",        expectTool:null },
  { id:'chat-bought',     say:"I bought a shovel.",                   expectTool:null },
  { id:'chat-plan',       say:"We're gonna clear this whole floor.",  expectTool:null },
  { id:'polite-canyou',   say:"Can you grab that scrap?",             expectTool:'move_buddy', status:'ok: state=fetching_scrap deliver_to=ship', banned:['fetching scrap for the ship'] },
  { id:'order-fetch',     say:"Grab the scrap.",                      expectTool:'move_buddy', status:'ok: state=fetching_scrap deliver_to=ship', banned:['fetching scrap for the ship'] },
  { id:'order-follow',    say:"Come with me.",                        expectTool:'move_buddy', status:'ok: state=following target=eamonthomas', banned:['following eamonthomas'] },
  { id:'order-stay',      say:"Buddy, stay here.",                    expectTool:'move_buddy', status:'ok: state=holding_position', banned:['holding position'] },
  { id:'order-scout',     say:"Scout ahead.",                         expectTool:'move_buddy', status:'ok: state=scouting_ahead distance_metres=10', banned:['state=','distance_metres'] },
  // The status carries two credit figures: what it cost and what is left. As prose ("...for 15
  // credits. 30 left.") the model picked the wrong one and said "Fifteen credits left." with 30
  // remaining - a wrong number stated to the player, and the probe scored it ok because nothing
  // asserted on it. TerminalBuddy.BuyItem now names both figures.
  { id:'order-buy',       say:"Buy a flashlight.",                    expectTool:'buy_item',   status:'ok: bought=Flashlight qty=1 cost_credits=15 credits_left=30',
    banned:['fifteen credits left','15 credits left','fifteen left','15 left'] },
  { id:'order-door',      say:"Open door D6.",                        expectTool:'control_facility_object', status:'ok: door D6 open' },
  { id:'refuse-bug',      say:"Kill the bug on my head!",             expectTool:null },
  { id:'refuse-leech',    say:"Get this leech off me!",               expectTool:null },
  { id:'refuse-facility', say:"Come inside the facility with me.",    expectTool:null },
  { id:'refuse-charge',   say:"Take my flashlight and charge it.",    expectTool:null },
  { id:'refuse-jetpack',  say:"Can I have a jetpack?",                expectTool:null },
  // The status says only that the item was spawned. 5.1.1 answered "Flashlight's yours. Forty-five
  // credits left." The figure came from the turn context rather than being invented, but a spawn
  // costs nothing, so a credit figure here is both noise and a step towards quoting stale numbers.
  { id:'beg-flashlight',  say:"Please, can I please have a flashlight? I'm begging you.", expectTool:'spawn_item', status:'ok: spawned 1 Flashlight', banned:['45','forty-five','credit'] },
  { id:'truth-nothing',   say:"Anything dangerous near me?",          expectTool:null },
];

// Vocabulary the contract forbids Buddy from ever speaking aloud.
const BANNED = ['tool','function','feature','capability','sensor','parameter','not set up to',
  "i don't have a","there isn't a",'not supported','not something i can do',"i'm not able to",
  'no direct action','say the word','let me know','want me to',
  // Source leakage. The contract says never mention where the information came from, but 5.1.1
  // answered "Nothing dangerous is listed near you." - "listed" tells the crew he is reading a
  // list. Buddy is supposed to just know what is around him.
  // 'shows' is deliberately broad: the probe prints the sentence it tripped on, so an innocent
  // use is visible and cheap to judge, whereas a missed leak costs a whole run to find.
  'listed','according to','shows','reads as','on my list','in my list'];

// The contract offers these as examples of Buddy's own voice and adds "Different every time,
// because people do not repeat themselves word for word". The model uses them verbatim and
// repeats them across runs, so a player hears the same line for the same order all shift - the
// canned feel 5.0.0 set out to remove, arriving through the prompt instead of through C#.
// Reported, never enforced: saying "Going." is not wrong, saying only ever "Going." is.
const CONTRACT_EXAMPLE_LINES = ['right behind you.', 'parked.', 'going.', 'fine.'];

const usage = { in:0, out:0 };
let preambles = 0, toolTurns = 0, cannedLines = 0;
const refusalLengths = [];

function run(sc) {
  return new Promise((resolve) => {
    const ws = new WebSocket(`wss://api.openai.com/v1/realtime?model=${MODEL}`,
      { headers: { Authorization: `Bearer ${KEY}` } });
    const rec = { id:sc.id, say:sc.say, calls:[], preamble:[], reply:[], error:null };
    let phase = 1, done = false, callId = null;
    const fin = () => { if (!done) { done = true; try { ws.close(); } catch {} resolve(rec); } };
    const timer = setTimeout(() => { rec.error = rec.error || 'timeout'; fin(); }, 60000);

    ws.onerror = e => { rec.error = 'ws ' + (e?.message || ''); clearTimeout(timer); fin(); };
    ws.onopen = () => {
      ws.send(JSON.stringify({ type:'session.update', session:{ type:'realtime', model:MODEL,
        instructions:CONTRACT, tools:TOOLS, tool_choice:'auto', output_modalities:['text'],
        max_output_tokens:1200,
        audio:{ input:{ format:{ type:'audio/pcm', rate:24000 }, turn_detection:null } } } }));
      ws.send(JSON.stringify({ type:'conversation.item.create', item:{ type:'message', role:'system',
        content:[{ type:'input_text', text:TURN_CONTEXT }] } }));
      ws.send(JSON.stringify({ type:'conversation.item.create', item:{ type:'message', role:'user',
        content:[{ type:'input_text', text:sc.say }] } }));
      ws.send(JSON.stringify({ type:'response.create' }));
    };

    ws.onmessage = ev => {
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      if (m.type === 'response.output_text.done') {
        const t = (m.text || '').trim();
        if (t) (phase === 1 ? rec.preamble : rec.reply).push(t);
      }
      if (m.type === 'response.function_call_arguments.done') {
        rec.calls.push({ name:m.name, args:m.arguments });
        callId = m.call_id;
      }
      if (m.type === 'error') { rec.error = JSON.stringify(m.error || m).slice(0,200); clearTimeout(timer); fin(); }
      if (m.type === 'response.done') {
        rec.status = m.response?.status;
        if (rec.status !== 'completed') rec.detail = JSON.stringify(m.response?.status_details || {}).slice(0,200);
        const u = m.response?.usage;
        if (u) { usage.in += u.input_tokens || 0; usage.out += u.output_tokens || 0; }
        if (callId && phase === 1) {
          // Hand the status back exactly as OpenAiRealtimeVoiceClient does.
          ws.send(JSON.stringify({ type:'conversation.item.create', item:{
            type:'function_call_output', call_id:callId, output: JSON.stringify({
              private_status: sc.status || 'ok',
              note: 'Status data. Never read aloud or paraphrase. Answer in your own words.' }) } }));
          callId = null; phase = 2;
          // The client asks for the post-tool response explicitly (output_modalities:["audio"] in
          // ProcessTurnAsync) because left to itself the model sometimes returns nothing after a
          // function result - the action happens and Buddy says nothing at all. A bare
          // response.create here did not mirror that, so an empty phase-2 reply in this probe
          // looked like a model failure when it was really the harness diverging from the mod.
          // Text, not audio, only to keep the run cheap.
          ws.send(JSON.stringify({ type:'response.create', response:{ output_modalities:['text'] } }));
          return;
        }
        clearTimeout(timer); fin();
      }
    };
  });
}

const unknown = ONLY.filter(id => !SCENARIOS.some(s => s.id === id));
if (unknown.length) { console.error('unknown scenario id(s): ' + unknown.join(',')); process.exit(2); }
const selected = ONLY.length ? SCENARIOS.filter(s => ONLY.includes(s.id)) : SCENARIOS;
// A filtered run that matched nothing must not report success.
if (!selected.length) { console.error('no scenarios selected'); process.exit(2); }

const plan = [];
for (let i = 0; i < REPEAT; i++) for (const sc of selected) plan.push(sc);

// PREAMBLE and INFO are observations about the model, not defects in the mod. They must never
// decide the exit code, or a run stops meaning "the behaviour is right" - and a row carrying only
// these must not print FAIL, which is how an informational note gets mistaken for a regression.
const informational = f => f.startsWith('PREAMBLE') || f.startsWith('INFO');

const sleep = ms => new Promise(x => setTimeout(x, ms));
const GAP = parseInt(process.env.BUDDY_PROBE_GAP_MS || '12000', 10);

// A rate-limited turn comes back with status "failed" and empty text, which is indistinguishable
// from the model refusing to speak. Treating that as a behaviour result is how a previous run
// scored 6/14. Retry it with backoff instead so every reported failure is a real one.
function rateLimited(r) {
  const blob = (r.detail || '') + ' ' + (r.error || '');
  return blob.includes('rate_limit_exceeded');
}

async function runWithRetry(sc) {
  let r;
  for (let attempt = 0; attempt <= 3; attempt++) {
    r = await run(sc);
    if (!rateLimited(r)) return r;
    const wait = 30000 * (attempt + 1);
    console.log(`     (rate limited on ${sc.id}; waiting ${wait / 1000}s, retry ${attempt + 1}/3)`);
    await sleep(wait);
  }
  return r; // still limited after retries - reported, and clearly labelled as such
}

const rows = [];
for (const sc of plan) {
  const r = await runWithRetry(sc);
  await sleep(GAP); // 40k TPM; see README.
  const called = r.calls.map(c => c.name);
  const spoken = (r.reply.length ? r.reply : r.preamble).join(' ').trim();
  const fails = [];
  if (sc.expectTool === null && called.length) fails.push('called ' + called.join(',') + ' on conversation');
  if (sc.expectTool && !called.includes(sc.expectTool)) fails.push('did not call ' + sc.expectTool);
  for (const b of (sc.banned || [])) if (spoken.toLowerCase().includes(b.toLowerCase())) fails.push(`parroted "${b}"`);
  for (const w of BANNED) if (spoken.toLowerCase().includes(w)) fails.push(`banned "${w}"`);
  if (r.error) fails.push('ERROR ' + r.error);
  if (r.status && r.status !== 'completed') fails.push('status=' + r.status + ' ' + (r.detail || ''));
  if (!spoken && !r.error) fails.push('no reply');
  if (called.length) { toolTurns++; if (r.preamble.length) { preambles++; fails.push('PREAMBLE: "' + r.preamble.join(' ') + '"'); } }
  if (CONTRACT_EXAMPLE_LINES.includes(spoken.trim().toLowerCase())) {
    cannedLines++;
    fails.push('INFO: reply is a contract example line verbatim');
  }
  // The contract asks for a refusal of one line with no second sentence. Reported, not enforced:
  // the model reliably answers in two short sentences and they are in character, so this is data
  // for deciding whether to tighten the prompt or relax the rule - not a reason to fail a build.
  if (sc.id.startsWith('refuse-') && spoken) {
    const n = spoken.split(/[.!?]+/).filter(s => s.trim()).length;
    refusalLengths.push(n);
    if (n > 1) fails.push(`INFO: refusal ran ${n} sentences`);
  }
  rows.push({ ...r, called, spoken, fails });
  const realFails = fails.filter(f => !informational(f));
  console.log(`${realFails.length ? 'FAIL' : (fails.length ? 'note' : 'ok  ')} ${sc.id.padEnd(16)} ` +
              `call=${called.join(',') || '-'} | "${spoken}"` +
              (fails.length ? '\n       -> ' + fails.join('; ') : ''));
}

const fullRun = !ONLY.length && REPEAT === 1;
if (fullRun) fs.writeFileSync(path.join(HERE, 'probe-results.json'), JSON.stringify(rows, null, 1));
else console.log('(filtered/repeated run - probe-results.json left untouched)');
const hard = rows.filter(r => r.fails.some(f => !informational(f)));
console.log(`\n${rows.length - hard.length}/${rows.length} behaviour checks passed`);
console.log(`model spoke before calling on ${preambles}/${toolTurns} tool turns (mod discards these)`);
console.log(`replies that were a contract example verbatim: ${cannedLines}/${rows.length}`);
if (refusalLengths.length) {
  const multi = refusalLengths.filter(n => n > 1).length;
  console.log(`refusals over one sentence: ${multi}/${refusalLengths.length} ` +
              `(contract asks for one line; reported, not enforced)`);
}
console.log(`tokens in=${usage.in} out=${usage.out}`);
process.exit(hard.length ? 1 : 0);
