#!/usr/bin/env python3
"""Package Phoenix text resources; build-time tool, never used by the server."""
import argparse, gzip, hashlib, json, pathlib, subprocess
parser = argparse.ArgumentParser()
parser.add_argument('reference', type=pathlib.Path)
args = parser.parse_args()
root = args.reference.resolve()
revision = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
files = {}
for folder in ['packages/nlu/resources/grammar', 'packages/nlu/resources/rules', 'packages/nlu/resources/rules-src',
               'packages/nlu/resources/factory', 'packages/nlu/resources/factory-sources',
               'packages/nlu/resources/factory-words', 'packages/nlu/resources/data',
               'packages/gateway/resources/skills/be-skills', 'packages/gateway/resources/skills/pegasus-skills',
               'packages/skills/resources/mims']:
    for path in sorted((root / folder).rglob('*')):
        if path.is_file() and path.suffix in {'.rule', '.grm', '.txt', '.json', '.mim', '.csv'}:
            files[str(path.relative_to(root))] = path.read_text()
for name in ['packages/nlu/src/generatedIntentCatalog.json', 'packages/nlu/resources/rule-inventory.json', 'packages/nlu/test/fixtures/launch-oracle-89.json']:
    files[name] = (root / name).read_text()
bundle = {'revision': revision, 'files': files}
repo = pathlib.Path(__file__).resolve().parents[2]
out = repo / 'src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Data/native-conversation.json.gz'
out.write_bytes(gzip.compress(json.dumps(bundle, ensure_ascii=False, separators=(',', ':')).encode(), mtime=0))
hashes = {name: hashlib.sha256(value.encode()).hexdigest() for name, value in files.items()}
(repo / 'docs/native-conversation-reference.json').write_text(json.dumps({'revision': revision, 'sha256': hashes}, indent=2) + '\n')
print(f'Pinned {revision}: {len(files)} text resources, {out.stat().st_size} compressed bytes')

# Inventory records catalog-only names separately from registered executable commands.
registrations = []
for name, content in files.items():
    if name.startswith('packages/gateway/resources/skills/') and name.endswith('_manifest.json'):
        manifest = json.loads(content)
        for command in manifest.get('intents', []):
            registrations.append({'skill': manifest['id'], 'onRobot': bool(manifest.get('onRobot')), **command})
catalog = json.loads(files['packages/nlu/src/generatedIntentCatalog.json'])['tools']
globals_ = {'beginning','close','end','help','holdOn','left','mainMenu','overHere','repeat','right','selectItem','sleep','thanks','turnAround','turnAway','volumeDown','volumeToValue','volumeUp','unknown'}
inventory = []
for tool in catalog:
    entries = [r for r in registrations if r['name'] == tool['name']]
    category = 'global' if tool['scope'] == 'global' else 'contextual' if not tool['launch'] else 'robot-launch' if any(r['onRobot'] for r in entries) else 'cloud-response' if entries else 'global' if tool['name'] in globals_ else 'catalog-only'
    inventory.append({'intent': tool['name'], 'category': category, 'launch': tool['launch'], 'scope': tool['scope'], 'domains': tool['domains'], 'rules': tool['source']['fst'], 'entities': tool['entities'], 'handlers': sorted({r['skill'] for r in entries}), 'description': tool['description']})
(repo / 'docs/native-command-inventory.json').write_text(json.dumps({'revision': revision, 'commands': inventory, 'registeredHandlers': registrations}, indent=2) + '\n')
