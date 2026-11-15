#!/usr/bin/env python3
"""Print units for translating:  listing.py <units.json> <from-id> <to-id> [groups] [--done dir]"""
import json, sys, os, glob
U = json.load(open(sys.argv[1], encoding='utf-8'))
a, b = int(sys.argv[2]), int(sys.argv[3])
groups = sys.argv[4].split(',') if len(sys.argv) > 4 and not sys.argv[4].startswith('--') else ['display', 'maybe']
done = set()
if '--done' in sys.argv:
    for f in glob.glob(os.path.join(sys.argv[sys.argv.index('--done') + 1], '*.tsv')):
        for line in open(f, encoding='utf-8'):
            if '\t' in line: done.add(line.split('\t')[0].strip())
last = None
n = chars = 0
for u in U:
    if not (a <= u['id'] <= b) or u['group'] not in groups or str(u['id']) in done: continue
    sc = u['scopes'][0]
    if sc != last:
        print('## ' + sc.replace('RpgRuntime::', ''))
        last = sc
    flag = ('c' if u['kind'] == 'chain' else '') + ('t' if u.get('term') else '')
    extra = ''
    if u.get('inside'): extra = '   « ' + u['inside'][0][:70]
    print(f"{u['id']}{flag}\t{json.dumps(u['text'], ensure_ascii=False)[1:-1]}{extra}")
    n += 1; chars += len(u['text'])
print(f'-- {n} units, {chars} chars', file=sys.stderr)
