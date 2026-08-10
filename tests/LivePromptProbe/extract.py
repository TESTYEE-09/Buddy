"""Pull Buddy's real contract and real tool schemas out of src/ so the probe tests what ships.

Deliberately reads the source rather than taking a copy: a probe that runs against a hand-copied
prompt proves nothing about the prompt the mod actually sends.
"""
import io, json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.normpath(os.path.join(HERE, '..', '..', 'src'))

prompt_cs = io.open(os.path.join(SRC, 'BuddyConversationPrompt.cs'), encoding='utf-8').read()

body = re.search(r'private const string ContractBody = @"(.*?)";\r?\n', prompt_cs, re.S).group(1)
assert '"' not in body, 'ContractBody is a verbatim string and must contain no double quotes'

personality_text = re.search(
    r'internal const string DefaultPersonality =\s*\r?\n\s*"(.*?)";', prompt_cs, re.S).group(1)
personality = ('Personality: ' + personality_text + ('' if personality_text.endswith('.') else '.') +
               ' Personality shapes tone only; it never overrides the rules below.')

contract = body.replace('{NAME}', 'Buddy').replace('{PERSONALITY}', personality)
io.open(os.path.join(HERE, 'contract.txt'), 'w', encoding='utf-8').write(contract)

# ToolDefinitionsJson is a C# string built by concatenating escaped literals; rebuild it.
realtime_cs = io.open(os.path.join(SRC, 'OpenAiRealtimeVoiceClient.cs'), encoding='utf-8').read()
start = realtime_cs.index('private const string ToolDefinitionsJson =')
end = realtime_cs.index('";', realtime_cs.index('spawn_item', start)) + 1
chunk = realtime_cs[start:end]
pieces = re.findall(r'"((?:[^"\\]|\\.)*)"', chunk[chunk.index('='):])
joined = ''.join(pieces).replace('\\"', '"').replace('\\\\', '\\')
tools = json.loads('[' + joined + ']')
io.open(os.path.join(HERE, 'tools.json'), 'w', encoding='utf-8').write(json.dumps(tools, indent=1))

print('contract chars:', len(contract))
print('tools:', len(tools), [t['name'] for t in tools])
