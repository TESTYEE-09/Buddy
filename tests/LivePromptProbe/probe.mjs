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
  { id:'order-buy',       say:"Buy a flashlight.",                    expectTool:'buy_item',   status:'Bought 1 Flashlight for 15 credits. 30 left.' },
  { id:'order-door',      say:"Open door D6.",                        expectTool:'control_facility_object', status:'ok: door D6 open' },
  { id:'refuse-bug',      say:"Kill the bug on my head!",             expectTool:null },
  { id:'refuse-leech',    say:"Get this leech off me!",               expectTool:null },
  { id:'refuse-facility', say:"Come inside the facility with me.",    expectTool:null },
  { id:'refuse-charge',   say:"Take my flashlight and charge it.",    expectTool:null },
  { id:'refuse-jetpack',  say:"Can I have a jetpack?",                expectTool:null },
  { id:'beg-flashlight',  say:"Please, can I please have a flashlight? I'm begging you.", expectTool:'spawn_item', status:'ok: spawned 1 Flashlight' },
  { id:'truth-nothing',   say:"Anything dangerous near me?",          expectTool:null },
];

// Vocabulary the contract forbids Buddy from ever speaking aloud.
const BANNED = ['tool','function','feature','capability','sensor','parameter','not set up to',
  "i don't have a","there isn't a",'not supported','not something i can do',"i'm not able to",
  'no direct action','say the word','let me know','want me to'];

const usage = { in:0, out:0 };
let preambles = 0, toolTurns = 0;

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
          ws.send(JSON.stringify({ type:'response.create' }));
          return;
        }
        clearTimeout(timer); fin();
      }
    };
  });
}

const rows = [];
for (const sc of SCENARIOS) {
  const r = await run(sc);
  await new Promise(x => setTimeout(x, 8000)); // 40k TPM; see README.
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
  rows.push({ ...r, called, spoken, fails });
  console.log(`${fails.length ? 'FAIL' : 'ok  '} ${sc.id.padEnd(16)} call=${called.join(',') || '-'} | "${spoken}"` +
              (fails.length ? '\n       -> ' + fails.join('; ') : ''));
}

fs.writeFileSync(path.join(HERE, 'probe-results.json'), JSON.stringify(rows, null, 1));
const hard = rows.filter(r => r.fails.some(f => !f.startsWith('PREAMBLE')));
console.log(`\n${rows.length - hard.length}/${rows.length} behaviour checks passed`);
console.log(`model spoke before calling on ${preambles}/${toolTurns} tool turns (mod discards these)`);
console.log(`tokens in=${usage.in} out=${usage.out}`);
process.exit(hard.length ? 1 : 0);
